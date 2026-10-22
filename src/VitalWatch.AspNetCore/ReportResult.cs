namespace VitalWatch;

/// <summary>
/// Gönderim sonucu. Yalnızca teşhis komutu ve testler okur; izleme yolu görmezden gelir.
/// </summary>
/// <param name="Sent">Hub'a ulaştı mı (HTTP &lt; 400). Hub geçersiz imzaya da 204 döner;
/// <c>true</c> "kabul edildi" değil, "istek ulaştı" demektir.</param>
/// <param name="Status">HTTP durum kodu; ağ hatasında <c>null</c>.</param>
/// <param name="Error">Ulaşmadıysa sebep.</param>
public readonly record struct ReportResult(bool Sent, int? Status, string? Error)
{
    internal static ReportResult Failed(string error) => new(false, null, error);
}
