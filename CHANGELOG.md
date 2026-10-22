# Değişiklik günlüğü

Semver. `0.x` sürümlerde **minor sıçraması kırıcı sayılır**. Yükseltirken:

```bash
dotnet add package VitalWatch.AspNetCore
dotnet run -- --vitalwatch-status
```

---

## 0.1.0

İlk sürüm. Kardeş paketlerle (`vitalwatch-node`, `vitalwatch` Python,
`cestnytr/vitalwatch-laravel`) aynı tel sözleşmesi, aynı maskeleme, aynı
ortam değişkenleri.

### Eklendi

- `builder.Services.AddVitalWatch()` + `app.UseVitalWatch()` — iki satır kurulum.
  Yapılandırma `VitalWatch` bölümünden ve `VITALWATCH_*` ortam değişkenlerinden;
  süreç ortamı appsettings'i yener, koddan verilen ayar ikisini de.
- Olaylar: `exception` (rota deseniyle: `GET /api/products/{id}`), `http_5xx`,
  `slow_request` (sorgu sayısı ve en yavaş sorguyla), `slow_query`, `job_failed`
  (çöken `BackgroundService`), `fatal` (bellek tükenmesi).
- SQL ölçümü paket bağımlılığı olmadan: EF Core ve SqlClient `DiagnosticListener`,
  Npgsql `ActivitySource`. İstek başına sayaç `AsyncLocal` ile; EF + sürücü aynı
  komutu iki kez saymaz.
- `UseExceptionHandler`'dan önce ya da sonra eklenebilir; işleyicinin 4xx'e
  çevirdiği istisna raporlanmaz.
- Sınırlı kuyruk + tek arka plan göndericisi; istek başına en çok 8 olay ve 8 KB
  (sığmayan olayda önce stack, sonra mesaj kırpılır).
- 8 saatlik canlılık isteği (`{"events":[]}`), başlangıçta hemen bir tane.
- Sürüm etiketi kendiliğinden: `VITALWATCH_RELEASE` → CI değişkenleri → `.git`
  (içerik kökü ve en çok üç üst dizin).
- Teşhis: `dotnet run -- --vitalwatch-status [--test] [--vitalwatch]`
  (`app.RunVitalWatchStatusAsync(args)`), eksik yapılandırmada tek başlangıç uyarısı.
- Elle bildirim: `IVitalWatch` (enjekte) ve `VitalWatchSdk.Report(ex)` (statik).
- Maskeleme kardeş paketlerle birebir aynı ve ortak vektörlerle (`Fixtures/mask-vectors.json`)
  sınanıyor. Uzun dizi maskesinde **düz CamelCase adlar jeton sayılmaz**: rakam, `-`/`_`
  taşımayan ve `^[A-Za-z][a-z]+(?:[A-Z][a-z]+)+$` desenine tamamen uyan aday olduğu gibi
  kalır — `InvalidOperationException`, `HandleNonSuccessAndDebuggerNotification` gibi
  yığın izi adları `[token]` olmaz. Rakamlı, ardışık büyük harfli ya da tümü büyük harf
  diziler maskelenir. Kural 22 Ekim 2026'da bütün SDK'lara ve hub'a birlikte geldi; öbür
  paketlerde böyle ad içeren mesajlar yükseltmeden sonra bir kez yeni parmak izi altında
  toplanır ve bir kez "yeni hata" uyarısı üretebilir.
