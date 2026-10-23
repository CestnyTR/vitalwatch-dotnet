using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VitalWatch.Internal;

/// <summary>
/// Çözümlenmiş paket listesini <c>/api/i/{key}/deps</c> ucuna gönderir; hub OSV.dev'e sorar.
/// </summary>
/// <remarks>
/// <para>Zamanlama: başlangıçtan <see cref="InitialDelay"/> sonra ilk gönderim (başlangıç
/// trafiğine karışmasın), sonra her <see cref="CheckInterval"/>'da dosya yeniden denetlenir;
/// hash değiştiyse ya da son başarılı gönderimden <see cref="ResendInterval"/> geçtiyse
/// gönderilir. Başarısız gönderim (ağ) bir sonraki denetimde yeniden denenir.</para>
/// <para>Davranış garantileri: hiçbir üye fırlatmaz, istek yolunda çalışmaz, kapanışta iptal
/// edilir. Gövde <see cref="MaxBodyBytes"/>'ı aşarsa hiç gönderilmez, tek uyarı yazılır.</para>
/// </remarks>
internal sealed class DependencyReporter : IDisposable
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromHours(24);

    /// <summary>Hub 512 KB'a kadar okur; pay bırakılır.</summary>
    public const int MaxBodyBytes = 500 * 1024;

    private readonly Reporter _reporter;
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _stop;
    private Task? _loop;
    private DependencyManifest? _cached;
    private (DateTime Modified, long Length)? _cachedStamp;
    private string? _lastSentHash;
    private DateTimeOffset? _lastSentAt;
    private int _warnedTooLarge;

    public DependencyReporter(Reporter reporter, string? path, TimeProvider? time = null, ILogger? logger = null)
    {
        _reporter = reporter;
        _path = path;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>deps.json'ın tam yolu (testte değiştirilir).</summary>
    public string? Path => _path;

    /// <summary>Arka plan görevi çalışıyor mu (test için).</summary>
    internal Task? Loop => _loop;

    /// <summary>Listeyi okur; dosya değişmediyse önbellekten. Fırlatmaz.</summary>
    public DependencyManifest Read()
    {
        try
        {
            if (!string.IsNullOrEmpty(_path) && File.Exists(_path))
            {
                var info = new FileInfo(_path);
                var stamp = (info.LastWriteTimeUtc, info.Length);

                lock (_gate)
                {
                    if (_cached is not null && _cachedStamp == stamp)
                    {
                        return _cached;
                    }
                }

                var manifest = DependencyManifest.Load(_path);

                lock (_gate)
                {
                    _cached = manifest;
                    _cachedStamp = stamp;
                }

                return manifest;
            }
        }
        catch
        {
            // Aşağıda "bulunamadı".
        }

        return DependencyManifest.Load(_path);
    }

    /// <summary>
    /// Arka plan döngüsünü başlatır. Kapalıysa, yapılandırma eksikse ya da zaten çalışıyorsa
    /// hiçbir şey yapmaz.
    /// </summary>
    public void Start()
    {
        try
        {
            if (!_reporter.Active || !_reporter.Settings.Dependencies || _loop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var token = _stop.Token;

            // Uygulamanın Activity/AsyncLocal bağlamı döngüye taşınmasın.
            using (ExecutionContext.SuppressFlow())
            {
                _loop = Task.Run(() => RunAsync(token));
            }
        }
        catch
        {
            // İzleme kurulamadıysa uygulama yine çalışır.
        }
    }

    public void Stop()
    {
        try
        {
            _stop?.Cancel();
        }
        catch
        {
            // Sessiz.
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(InitialDelay, _time, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                await TickAsync(token).ConfigureAwait(false);
                await Task.Delay(CheckInterval, _time, token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Kapanış ya da beklenmedik hata — sessiz.
        }
    }

    /// <summary>
    /// Bir denetim turu: gerekiyorsa gönderir. Gönderim yapıldıysa sonucu, yapılmadıysa
    /// <c>null</c> döndürür. Fırlatmaz.
    /// </summary>
    internal async Task<ReportResult?> TickAsync(CancellationToken token)
    {
        try
        {
            if (!_reporter.Active || !_reporter.Settings.Dependencies)
            {
                return null;
            }

            var manifest = Read();
            if (!manifest.Found)
            {
                return null;
            }

            var now = _time.GetUtcNow();
            var due = _lastSentHash is null
                || !string.Equals(_lastSentHash, manifest.Hash, StringComparison.Ordinal)
                || _lastSentAt is null
                || now - _lastSentAt.Value >= ResendInterval;

            if (!due)
            {
                return null;
            }

            var result = await SendAsync(manifest, token).ConfigureAwait(false);

            if (result.Sent)
            {
                _lastSentHash = manifest.Hash;
                _lastSentAt = now;
            }

            return result;
        }
        catch (Exception e)
        {
            return ReportResult.Failed(e.Message);
        }
    }

    /// <summary>Listeyi hemen gönderir (teşhis komutu <c>--deps</c> da bunu kullanır). Fırlatmaz.</summary>
    public async Task<ReportResult> SendAsync(DependencyManifest manifest, CancellationToken token = default)
    {
        try
        {
            if (!_reporter.Active)
            {
                return ReportResult.Failed(_reporter.Settings.Enabled ? "missing-config" : "disabled");
            }

            if (!manifest.Found)
            {
                return ReportResult.Failed("deps-not-found");
            }

            var body = Encode(manifest, _reporter.Settings);

            if (body.Length > MaxBodyBytes)
            {
                // Tek uyarı: her denetim turunda aynı satırı yazmak günlüğü kirletir.
                if (Interlocked.Exchange(ref _warnedTooLarge, 1) == 0)
                {
                    _logger?.LogWarning(
                        "VitalWatch dependency list was not sent: {Bytes} bytes exceeds the {Limit} byte limit ({Count} packages in {Manifest}).",
                        body.Length,
                        MaxBodyBytes,
                        manifest.Packages.Count,
                        manifest.FileName);
                }

                return ReportResult.Failed("body-too-large");
            }

            return await _reporter.Client.SendDependenciesAsync(body, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return ReportResult.Failed(e.Message);
        }
    }

    /// <summary>
    /// <c>{ kimlik…, "dependencies": { "v":1, "ecosystem", "manifest", "hash", "packages":[…] } }</c>.
    /// Kimlik alanları <c>/server</c> ucunun üst seviyesindekilerle aynı.
    /// </summary>
    internal static byte[] Encode(DependencyManifest manifest, Settings settings)
    {
        using var stream = new MemoryStream(4096 + manifest.Packages.Count * 48);
        using (var writer = new Utf8JsonWriter(stream, PayloadEncoder.WriterOptions))
        {
            writer.WriteStartObject();
            PayloadEncoder.WriteIdentity(writer, settings, includeEnv: true);

            writer.WriteStartObject("dependencies");
            writer.WriteNumber("v", 1);
            writer.WriteString("ecosystem", DependencyManifest.Ecosystem);
            writer.WriteString("manifest", manifest.FileName);
            writer.WriteString("hash", manifest.Hash);
            writer.WriteStartArray("packages");

            foreach (var package in manifest.Packages)
            {
                writer.WriteStartObject();
                writer.WriteString("name", package.Name);
                writer.WriteString("version", package.Version);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        Stop();
        _stop?.Dispose();
    }
}
