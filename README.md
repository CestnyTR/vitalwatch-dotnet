# VitalWatch.AspNetCore

[VitalWatch Hub](https://github.com/CestnyTR/vitalwatch-hub) için ASP.NET Core raporlayıcı:
yakalanmamış hatalar, 5xx yanıtlar, yavaş istekler, yavaş SQL sorguları ve çöken arka plan
servisleri.

.NET 8 ve .NET 10. **Çalışma zamanı bağımlılığı yok** — yalnızca ASP.NET Core paylaşılan
çerçevesi. EF Core, SqlClient ya da Npgsql paketine bağımlı değildir; varsa onları dinler.

Kardeş paketler: [`vitalwatch-node`](https://github.com/CestnyTR/vitalwatch-node) (Node),
[`vitalwatch`](https://github.com/CestnyTR/vitalwatch-python) (Python),
[`cestnytr/vitalwatch-laravel`](https://github.com/CestnyTR/vitalwatch-laravel) (Laravel),
`t.js` (tarayıcı, hub'dan servis edilir). Aynı ortam değişkenleri, aynı maskeleme, aynı
tel sözleşmesi.

---

## Kurulum

```bash
dotnet add package VitalWatch.AspNetCore
```

`Program.cs` — iki satır:

```csharp
using VitalWatch;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVitalWatch();          // 1

var app = builder.Build();
app.UseVitalWatch();                       // 2 — mümkün olduğunca erken

if (await app.RunVitalWatchStatusAsync(args)) return;   // isteğe bağlı: teşhis komutu

// ... uygulamanın geri kalanı
app.Run();
```

`UseVitalWatch()` süre ölçümünü eklendiği noktadan başlatır; `builder.Build()`'den hemen
sonra ekleyin. `UseExceptionHandler`'dan önce ya da sonra olması fark etmez (bkz.
[Hata işleyicisiyle birlikte](#hata-işleyicisiyle-birlikte)).

### Yapılandırma

Ortam değişkenleri — kardeş paketlerle aynı adlar:

```env
VITALWATCH_URL=https://vitalwatch.example.com
VITALWATCH_KEY=proje-anahtari
VITALWATCH_SECRET=<panelden alınan 64 karakterlik secret>
VITALWATCH_ENV=production
```

ya da `appsettings.json`:

```json
{
  "VitalWatch": {
    "Url": "https://vitalwatch.example.com",
    "Key": "proje-anahtari",
    "Env": "production"
  }
}
```

**Secret'i `appsettings.json`'a yazmayın** — ortam değişkeni, user-secrets ya da gizli
anahtar kasası kullanın. Depoya giren secret, depoyu okuyan herkesin elindedir.

Öncelik (sonraki öncekini ezer):

1. `appsettings*.json` → `VitalWatch` bölümü
2. Süreç ortamındaki `VITALWATCH_*` — **süreç ortamı her zaman kazanır** (kardeş paketlerdeki kural)
3. Koddan: `AddVitalWatch(o => { ... })`

```csharp
builder.Services.AddVitalWatch(o =>
{
    o.SlowRequestMs = 2000;
    o.Ignore.Add(typeof(OperationCanceledException));
});
```

### Kurulumu doğrulayın

```bash
dotnet run -- --vitalwatch-status            # yapılandırmayı gösterir
dotnet run -- --vitalwatch-status --test     # gerçek bir sınama hatası gönderir
dotnet run -- --vitalwatch-status --vitalwatch   # panele hata düşürmeden canlılık isteği
# yayınlanmış uygulamada:
dotnet Uygulama.dll --vitalwatch-status --test
```

```
  SDK sürümü         0.1.0
  Güncel sürüm       0.1.0
  Etkin              evet
  Hub adresi         https://vitalwatch.example.com
  Proje anahtarı     proje-anahtari
  Secret uzunluğu    64 karakter
  Environment        production
  Sürüm etiketi      a1b2c3d4e5f6 (kaynak: .git)
  .NET               10.0.12

  ✓ Yapılandırma tamam.
  ✓ Sınama olayı gönderildi (HTTP 204).
```

Komut uygulamanın **kendi** host'unda çalışır, web sunucusunu başlatmaz ve çıkar
(sorun varsa çıkış kodu 1). Ayrı bir `dotnet tool` değil, çünkü öyle bir araç uygulamanın
gördüğü yapılandırmayı göremezdi: `appsettings.Production.json`, user-secrets, kasa ve
koddan verilen ayarlar ancak uygulamanın host'unda birleşir. Teşhis aracının yanlış teşhis
koyması, hiç teşhis koymamaktan kötüdür. HTTP ucu da değil: yapılandırmayı internete açardı.

`RunVitalWatchStatusAsync` satırını eklemeseniz de yapılandırma eksik ya da hatalıysa
(URL/anahtar/secret yok, secret 64 karakter değil, `http://` adres, tanınmayan ortam)
uygulama başlarken **tek bir uyarı** günlüğe yazılır.

> Hub geçersiz imzaya da 204 döner. Verinin gerçekten ulaştığı yalnızca hub panelinden
> doğrulanır: proje satırında bağlantı durumu "Bağlı" görünmelidir.

`VITALWATCH_STATUS_OFFLINE=1` NuGet'te güncel sürüm denetimini atlar.

---

## Ne toplar

| Tür | Ne zaman |
|---|---|
| `exception` | İstek hattında yakalanmamış istisna. Rota **deseniyle**: `GET /api/products/{id}` |
| `http_5xx` | İstisnasız 5xx yanıt (`Results.StatusCode(503)` …) |
| `slow_request` | `SlowRequestMs`'i aşan istek: süre, durum, sorgu sayısı, en yavaş sorgu |
| `slow_query` | `SlowQueryMs`'i aşan SQL komutu, literal değerleri `?` ile |
| `job_failed` | Çöken `BackgroundService` / hosted service; "Yol" sütununda servis türü |
| `fatal` | Bellek tükenmesi; süreci düşüren yakalanmamış istisna `exception` olarak |

Her olayda: istisna sınıfı, maskelenmiş mesaj ve stack, pdb varsa dosya ve satır, rota
deseni, yöntem, ortam, sürüm etiketi, `runtime: "dotnet"`, .NET sürümü, SDK sürümü.

- **Tek arıza, tek kayıt.** İstisnalı istek yalnızca stack'li `exception` açar; aynı istek
  için ayrıca `http_5xx` gönderilmez. Aynı istisna nesnesi hangi yoldan gelirse gelsin
  (ara katman, hata işleyicisi, günlük, elle bildirim) bir kez gider.
- **4xx gönderilmez.** `BadHttpRequestException` (400) ve uygulamanın `IExceptionHandler`'ı
  ile 4xx'e çevrilen istisna arıza değildir. İstemcinin kopması (iptal) da.
- **Hata yutulmaz.** İstisna kaydedilir ve aynı stack'le yeniden fırlatılır; hata
  sayfanız, `ProblemDetails`'ınız aynen çalışır.

### Hata işleyicisiyle birlikte

```csharp
app.UseVitalWatch();
app.UseExceptionHandler("/Error");   // önce ya da sonra — ikisi de çalışır
```

- `UseVitalWatch` **önce** ise istisna ara katmana hiç ulaşmaz; hata işleyicisinin yayınladığı
  tanılama olayından alınır ve yanıt 5xx ile biterse raporlanır.
- **Sonra** ise ara katman istisnayı kendisi görür ve yeniden fırlatır. İşleyici isteği
  `/Error`'a yeniden yürüttüğünde ikinci geçiş ölçülmez; panelde `/Error` değil, hatanın
  patladığı uç görünür.

### Yavaş SQL

Kurulum gerektirmez. Kaynaklar:

| Kaynak | Kapsam |
|---|---|
| EF Core `DiagnosticListener` | Bütün EF sağlayıcıları (SQL Server, PostgreSQL, SQLite, MySQL …) |
| SqlClient `DiagnosticListener` | Dapper / ham ADO.NET ile SQL Server (Microsoft.Data.SqlClient ve System.Data.SqlClient) |
| Npgsql `ActivitySource` | Dapper / ham ADO.NET ile PostgreSQL |

- Sorgu sayısı **istek başına** tutulur (`AsyncLocal`): eşzamanlı istekler birbirinin
  sorgularını görmez. `slow_request` olayında `query_count`, `slowest_query_sql`,
  `slowest_query_ms` gelir — N+1 sorununu yakalamanın yolu.
- SQL **normalize edilir**: `where email = 'ahmet@ornek.com' and id = 42` →
  `where email = ? and id = ?`. Hem KVKK gereği hem gruplama için: aynı sorgu farklı
  parametrelerle tek parmak izinde toplanır. PostgreSQL'in çift tırnaklı tanımlayıcıları da
  `?` olur — kardeş paketler ve hub aynısını yapıyor.
- EF Core ile SqlClient/Npgsql aynı komutu iki kez bildirir; paket bir kez sayar. Bilinen
  taviz: aynı istekte hem EF hem ham Npgsql (Dapper) kullanılıyorsa EF'den sonraki ham
  Npgsql sorguları sayılmaz.

### Arka plan servisleri

`BackgroundService.ExecuteAsync` fırlattığında .NET hiçbir kanca yayınlamıyor; tek iz
host'un kendi günlük kaydı. Paket bu yüzden küçük bir günlük sağlayıcısı ekler ve yalnızca
`Microsoft.Extensions.Hosting.Internal.Host` kategorisindeki **Error/Critical, istisnalı**
kayıtları dinler. Uygulamanızın diğer günlüklerine dokunmaz.

> `builder.Logging.ClearProviders()` kullanıyorsanız onu `AddVitalWatch()`'tan **önce**
> çağırın; sonra çağrılırsa paketin günlük sağlayıcısı da silinir ve `job_failed` gelmez.

### Elle bildirim

```csharp
// Enjekte ederek
app.MapPost("/import", async (IVitalWatch vitalWatch) =>
{
    try { /* ... */ }
    catch (Exception e)
    {
        vitalWatch.Report(e);   // kuyruğa bırakır, beklemez
        return Results.Problem();
    }
});

// DI dışından
VitalWatchSdk.Report(exception);
VitalWatchSdk.Report(exception, "command_failed");
```

İstek içinden çağrılırsa rota ve yöntem kendiliğinden eklenir, aynı istek için ayrıca
"HTTP 500" açılmaz. Statik sınıfın adı `VitalWatchSdk`: ad alanıyla aynı adı taşıyan bir sınıf
(`VitalWatch.Report`) `using VitalWatch;` yazılan dosyada derlenmezdi.

---

## Ne toplamaz

Bunlar yapılandırmayla dahi açılamaz — kodda karşılığı yoktur:

- IP adresi
- User-Agent
- İstek gövdesi / form verisi
- Query string **değerleri** (yalnızca yol saklanır; eşleşen rotada desen)
- Oturum verisi, çerez, `Authorization` başlığı
- Ham SQL literal değerleri

Mesaj ve stack gönderilmeden önce e-posta, TC kimlik no, telefon, IBAN, kart numarası ve
uzun rastgele diziler (oturum kimliği, API anahtarı) maskelenir. İç istisnaların mesajları
da (`DbUpdateException`'ın içindeki sürücü hatası) SQL temizliğinden geçer. Desenler kardeş
paketlerle birebir aynıdır ve hub'ın ürettiği ortak test vektörleriyle sınanır.

---

## Davranış garantileri — ihlal edilemez

1. **Hata yutulmaz.** İstisna `throw;` ile aynı stack'le yeniden fırlatılır.
2. **Kendi hatasını raporlamaz.** Bütün paket kodu kendi hatasını yutar; sonsuz döngü yok.
3. **Kullanıcı isteğini bekletmez.** İstek yalnızca olayı kuyruğa bırakır; gönderim tek bir
   arka plan görevinden, kısa zaman aşımıyla yapılır. Kuyruk sınırlıdır (100); dolarsa olay
   düşürülür — izleme paketinin uygulamanın belleğini yemesi kabul edilemez.
4. **Hub erişilemezse sessizce vazgeçilir.** Günlük kirliliği yok, yeniden deneme fırtınası yok.
5. **`VITALWATCH_ENABLED=false` iken hiçbir şey kurulmaz** — ne ara katman, ne günlük
   sağlayıcısı, ne tanılama aboneliği, ne arka plan görevi. Yeniden dağıtmadan izlemeyi
   kapatmanın yolu.
6. **Süreç yönetimine karışılmaz.** Süreci düşüren istisna raporlanır ama süreç yine düşer.

> Bir izleme paketinin izlediği uygulamayı bozması, çözdüğü sorundan büyük bir sorundur.

---

## Gönderim

- `POST {URL}/api/i/{KEY}/server`, `X-VitalWatch-Signature: sha256=hex(hmac_sha256(secret, "{timestamp}." + gövde))`
  ve `X-VitalWatch-Timestamp`. Yönlendirme takip edilmez.
- İstek başına **en çok 8 olay ve 8 KB** — hub fazlasını sessizce atar. Sığmayan olayda önce
  stack, sonra mesaj (200 karaktere) kırpılır; birden çok olay sığmıyorsa sonraki isteğe kalır.
- Aynı anda düşen olaylar 50 ms içinde tek istekte toplanır (hub'ın IP başına 60/dk sınırı).
- Uygulama kapanırken kuyruk en çok `zaman aşımı + 0,5 sn` (üst sınır 3 sn) boşaltılır.
- `traceparent` başlığı hub'a gönderilmez: izleme istekleri uygulamanızın dağıtık izine karışmaz.

## Canlılık nabzı

Paket sekiz saatte bir, olay taşımayan küçük bir istek (`{"events":[]}`) gönderir; ilki
uygulama başlar başlamaz. Hub bir kurulumun çalıştığını eskiden yalnızca gelen hatalardan
anlıyordu ve hatasız çalışan uygulama "kurulum bozuk" görünüyordu. Aralık, hub'ın 24 saatlik
sessizlik eşiğinin üçte biri: iki tur kaçsa bile alarm çalmaz. `HeartbeatInterval =
TimeSpan.Zero` kapatır.

---

## Yapılandırma

| Değişken | appsettings | Varsayılan | Açıklama |
|---|---|---|---|
| `VITALWATCH_ENABLED` | `Enabled` | `true` | `false` (ya da `0`/`no`/`off`) ise hiçbir şey kurulmaz |
| `VITALWATCH_URL` | `Url` | — | Hub adresi |
| `VITALWATCH_KEY` | `Key` | — | Panelden alınan proje anahtarı |
| `VITALWATCH_SECRET` | `Secret` | — | 64 karakter. **Yalnızca sunucuda bulunur** |
| `VITALWATCH_ENV` | `Env` | ASP.NET Core ortamı | `production` · `staging` · `local`. Takma adlar çevrilir: `Development`/`dev`/`test` → local, `Staging`/`stage`/`uat` → staging, `Production`/`prod`/`live` → production. Tanınmayan değeri hub reddeder; teşhis komutu hata verir |
| `VITALWATCH_RELEASE` | `Release` | kendiliğinden | Sürüm etiketi, bkz. [Sürüm etiketi](#sürüm-etiketi) |
| `VITALWATCH_TIMEOUT` | `Timeout` | `2000` | Gönderim zaman aşımı. **100 ve üstü milisaniye, altı saniye** (`2` → 2000 ms). 100–10000 ms aralığına sıkıştırılır; geçersiz, sıfır ya da negatif değer 2000 olur. Kural bütün paketlerde aynı |
| `VITALWATCH_SLOW_REQUEST_MS` | `SlowRequestMs` | `1000` | Bu süreyi aşan istek yavaş sayılır |
| `VITALWATCH_SLOW_QUERY_MS` | `SlowQueryMs` | `500` | Bu süreyi aşan SQL komutu yavaş sayılır |
| — | — | — | Yalnızca koddan: `Ignore` (yok sayılacak istisna türleri, alt sınıflar dahil), `HeartbeatInterval` |

### Sürüm etiketi

Kurulumda bir kez, şu sırayla bulunur (kardeş paketlerle aynı kural):

1. Açık değer: kod → `VITALWATCH_RELEASE` → `VitalWatch:Release`. Kırpılır, en çok 64 karakter.
2. İlk dolu CI/PaaS değişkeni: `GIT_COMMIT`, `GIT_SHA`, `COMMIT_SHA`, `SOURCE_VERSION`,
   `VERCEL_GIT_COMMIT_SHA`, `RENDER_GIT_COMMIT`, `HEROKU_SLUG_COMMIT`, `CI_COMMIT_SHA`.
   Sha biçimindeyse küçük harf ilk 12 karakter.
3. `.git`: `HEAD` → dal ref dosyası, yoksa `packed-refs`; ayrık HEAD doğrudan sha. `.git` bir
   dosyaysa (worktree, alt modül) içindeki `gitdir:` izlenir. İçerik kökünde yoksa **en çok üç
   üst dizine** bakılır — .NET projeleri çoğunlukla `repo/src/Uygulama` altında çalışır.
   Yalnızca geçerli 40 hanelik sha kabul edilir; ilk 12 karakter.
4. Hiçbiri yoksa etiket gönderilmez.

`git` programı çalıştırılmaz; yalnızca birkaç küçük dosya okunur. Teşhis komutu etiketi
kaynağıyla gösterir.

---

## Sorun giderme

| Belirti | Sebep / çözüm |
|---|---|
| Panelde hiçbir şey yok, uygulama sessiz | `dotnet run -- --vitalwatch-status --test`. Başlangıçta "VitalWatch yapılandırması eksik" uyarısı var mı bakın |
| Secret uzunluğu 64 değil | Kopyalarken başı/sonu eksik kalmış. Hub geçersiz imzaya da 204 döner; tek belirti budur |
| Olaylar geliyor, ortam yanlış | `VITALWATCH_ENV` boşsa ASP.NET Core ortamı kullanılır; `ASPNETCORE_ENVIRONMENT` tanınmayan bir değerse (`QA`) hub reddeder |
| `job_failed` gelmiyor | `ClearProviders()` `AddVitalWatch()`'tan sonra çağrılmış ya da `BackgroundService` istisnayı kendisi yakalıyor |
| `slow_request`'te `query_count` yok | `UseVitalWatch()` eklenmemiş ya da sorgular istekten önce/sonra çalışıyor (arka plan) |
| Stack'te `[token]` | Düz CamelCase adlar (`InvalidOperationException`, `HandleNonSuccessAndDebuggerNotification`) maskelenmez. 24+ karakterlik ad rakam, `_`/`-` ya da yan yana iki büyük harf taşıyorsa (`IOExceptionHandlerMiddleware1`) jeton deseniyle eşleşir. Desen bütün SDK'larda ve hub'da ortak; hub stack'i aynı kuralla yeniden maskeler |
| Uygulama kapanırken son hata gitmedi | Kuyruk kapanışta en çok 3 sn boşaltılır; süreç `kill -9` ile öldürülürse kuyruk kaybolur |

Geçici olarak kapatmak: `VITALWATCH_ENABLED=false` ve uygulamayı yeniden başlatın.

---

## Geliştirme

```bash
dotnet test                      # net8.0 + net10.0
dotnet test -f net10.0           # yalnızca .NET 10 çalışma zamanı kuruluysa
dotnet pack src/VitalWatch.AspNetCore -c Release -o artifacts
```

Testler hub'ın imza doğrulamasını birebir uygulayan sahte bir hub'a (Kestrel,
`127.0.0.1:0`) karşı koşar; "204 aldım" yeterli sayılmaz, imzanın ve 8 KB / 8 olay
sınırlarının tutulduğu doğrulanır. Maskeleme testleri hub'ın ürettiği ortak vektör dosyasını
(`tests/VitalWatch.AspNetCore.Tests/Fixtures/mask-vectors.json`) okur.

Yayın: `<Version>` güncellenir, `v0.1.0` gibi etiket gönderilir; iş akışı NuGet trusted
publishing ile yükler.

## Lisans

MIT
