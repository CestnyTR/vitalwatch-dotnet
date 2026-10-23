# vitalwatch-dotnet — Çalışma Kuralları

Bu paket (`VitalWatch.AspNetCore`, NuGet) **izlenen ASP.NET Core projelerine** kurulur. Hub
ayrı repodadır: [vitalwatch-hub](https://github.com/CestnyTR/vitalwatch-hub) — tel sözleşmesi
`vitalwatch-doc/API.md`, toplama ucu `IngestController` / `VerifyProjectSignature` /
`IngestService`.

Kardeş paketler: `cestnytr/vitalwatch-laravel` (Laravel), `vitalwatch-node` (Node),
`vitalwatch` (Python), `t.js` (tarayıcı, hub reposunda `resources/sdk/t.js`). Bir davranış
değiştirilmeden önce kardeş paketteki karşılığına bakılır; ayrışma bilinçli olmalı ve
aşağıdaki "Kardeşlerden ayrışan yerler" listesine yazılmalı.

## Dil

- **README, CHANGELOG ve paket açıklaması (`<Description>`, `<PackageTags>`) İngilizce.**
  Paketler public, kitle uluslararası — 22 Ekim 2026 kararı. Yeni CHANGELOG girdileri de
  İngilizce yazılır.
- **Kullanıcının gördüğü her çıktı İngilizce**: `--vitalwatch-status` raporu, günlük
  uyarıları (`VitalWatch` kategorisi), yapılandırma sorunu mesajları, kullanıcıya fırlatılan
  istisnalar, SDK'nın ürettiği olay `msg` şablonları ve **public API'nin XML dokümantasyonu**
  (`///` — nupkg ile IntelliSense'e gider). 22 Ekim 2026 kararı. Ortak etiketler kardeş
  paketlerle aynı: "SDK version", "Latest version", "Hub URL", "Project key", "Secret length",
  "Release tag", "(source: …)", "not set", "Configuration OK." …
- **İç kod yorumları Türkçe** kalır: `//`, `/* */` ve internal/private üyelerin `///`
  açıklamaları yalnızca bakımcı okur.
- Üretilen olay mesajı değişirse hub'da parmak izi bir kez değişir — CHANGELOG'a yazılır.
- **Sınıf, metot, özellik ve değişken adları İngilizce** — yerel değişkenler dahil.
- Kardeş paketlerle aynı kural.

---

## Bu paket public bir repodur

- **Hiçbir sır koda girmez.** Anahtar ve secret ortam değişkeninden ya da yapılandırmadan okunur.
- Testlerde gerçekçi sağlayıcı öneki taşıyan sahte jeton kullanılmaz — GitHub'ın gizli
  anahtar taraması push'u engeller. (Ortak vektör dosyasındaki tek önekli örnek kardeş
  paketlerde de aynen duruyor; yenisi eklenmez.)
- MIT lisanslı; NuGet'e trusted publishing ile yayınlanır (`.github/workflows/publish.yml`).

---

## Kapsam sınırı — bilinçli

Bu pakete yalnızca **sunucuda çalışan** kod girer: ASP.NET Core ara katmanı, hosted service
izleme, SQL ölçümü. Tarayıcı kodu girmez; `t.js` hub'dan servis edilir.

**Çalışma zamanı bağımlılığı yok.** Tek referans `FrameworkReference Microsoft.AspNetCore.App`.
EF Core, Microsoft.Data.SqlClient, Npgsql **paket olarak eklenmez**: olayları
`DiagnosticListener` / `ActivitySource` üzerinden, yükün özellikleri adla okunarak
(`PropertyFetcher`) dinlenir. Bu paket onlarca projeye kurulacak; her bağımlılık sürüm
çakışması riski. EF Core yalnızca test projesinde, gerçek yükün adlarını doğrulamak için var.

---

## Bağlayıcı kısıtlar

### Toplanmayacaklar

Yapılandırmayla dahi açılamaz — kodda karşılığı bulunmamalı:

- IP adresi
- User-Agent
- İstek gövdesi / form verisi
- Query string **değerleri** (yalnızca yol saklanır)
- Oturum verisi, çerez, `Authorization`
- Ham SQL literal değerleri

`MiddlewareTests.Ip_user_agent_query_cookies_and_body_are_never_sent` bunu ham gövde
üzerinden doğrular; yeni bir alan eklendiğinde bu test genişletilir.

### Temizlik desenleri kardeş paketlerle aynı kalır

`Internal/Scrubber.cs` içindeki desenler hub'la ve kardeş paketlerle birebir aynı davranmalıdır.
Ayrışırlarsa aynı hata iki SDK'da farklı maskelenir ve parmak izi bölünür.

.NET'e özgü tuzak: `\b`, `\d`, `\s` .NET'te Unicode farkındadır, PHP'de (bayraksız desen) ve
JS'de ASCII. Bu yüzden açık ASCII sınıflarıyla yazıldılar (`(?<![A-Za-z0-9_])`, `[0-9]`).
"Sadeleştirmek" için `\b`/`\d`'ye geri dönülmez; `ScrubberTests` bunu yakalar.

Ortak vektörler: `tests/.../Fixtures/mask-vectors.json` — kaynağı
`vitalwatch-doc/apps/vitalwatch-flutter/maske-vektorleri.json`. Hub tarafında güncellenirse
buraya kopyalanır.

---

## Davranış garantileri — ihlal edilemez

1. **Hata yutulmaz.** Ara katman `throw;` ile yeniden fırlatır (stack korunur).
2. **Süreç yönetimine karışılmaz.** `Environment.Exit` çağrılmaz; `AppDomain.UnhandledException`
   yalnızca dinlenir.
3. **Kendi hatasını raporlamaz.** Tanılama geri çağrıları, günlük sağlayıcısı, gönderici —
   paketin her giriş noktası kendi hatasını yutar.
4. **Kullanıcı isteğini bekletmez.** İstek yalnızca olayı sınırlı kuyruğa (`Channel`, 100)
   bırakır; dolarsa düşürülür. Ağ işi tek arka plan görevinde, kısa zaman aşımıyla.
5. **Hub erişilemezse sessizce vazgeçilir.**
6. **`VITALWATCH_ENABLED=false` iken hiçbir kanca kurulmaz** — `AddVitalWatch` yalnızca boş bir
   `IVitalWatch` kaydeder.

---

## Teknik tercihler

| Konu | Karar |
|---|---|
| Hedef | `net8.0;net10.0` |
| Bağımlılık | **Sıfır** — yalnızca paylaşılan çerçeve |
| HTTP | Paket içi `HttpClient` (`SocketsHttpHandler`, yönlendirme kapalı, `traceparent` yayılmaz) |
| JSON | `Utf8JsonWriter` elle — yansıma tabanlı serileştirme yok, Türkçe karakter kaçışsız |
| Gönderim | Sınırlı `Channel` + tek arka plan görevi; 50 ms toplama penceresi; ≤8 olay, ≤8 KB |
| Test | xUnit + TestHost; sahte hub Kestrel'de, HMAC'i hub gibi doğrular |
| Sürümleme | Semver, `v*` etiketi; etiket csproj'daki `<Version>` ile eşleşmezse yayın durur |

### Süreç boyunca tekil, istek bağlamı AsyncLocal

`Reporter` DI singleton. İstek bağlamı (`RequestScope`: sorgu sayacı, en yavaş sorgu, bekleyen
istisna) `AsyncLocal` ile taşınır ve `HttpContext.Items`'ta da durur. Süreç geneli sayaç
eşzamanlı isteklerin sorgularını karıştırırdı (`QueryTests.Query_count_is_isolated...`).

`DiagnosticListener.AllListeners` süreç geneli; abonelik `DiagnosticsBridge`'de **bir kez**
yapılır ve olay istek kapsamının raporlayıcısına yönlendirilir. Host başına abonelik aynı
sorguyu her host için ayrı raporlardı.

### Tekilleştirme

İşaret istisnanın üstünde: `Exception.Data["VitalWatch.Reported"]` (+ `ConditionalWeakTable`
yedeği). Aynı istisna ara katmandan, hata işleyicisinin tanılama olayından, host günlüğünden
ve elle bildirimden gelebilir; işaret hepsini tek kayda indirir.

---

## Kardeşlerden ayrışan yerler

Bilinçli; değiştirmeden önce sebebi okuyun.

- **Teşhis komutu ayrı araç değil**: `app.RunVitalWatchStatusAsync(args)` +
  `--vitalwatch-status`. Ayrı `dotnet tool` uygulamanın birleşik yapılandırmasını
  (appsettings.{Ortam}, user-secrets, kasa, kod) göremezdi — Node'da `.env` için yaşanan
  yanlış teşhisin aynısı.
- **`.env` okunmaz.** .NET'te yapılandırma kaynağı appsettings + ortam; `.env` geleneği yok.
- **`.git` üç üst dizine kadar aranır.** .NET projeleri `repo/src/Uygulama` altında çalışır.
- **`VITALWATCH_ENABLED`** `false`'a ek olarak `0`/`no`/`off` ile de kapanır (kapatma yönünde
  gevşeklik güvenli taraf).
- **`http://` uyarısı geri döngü adreste verilmez** (yerel hub ile geliştirme).
- **Toplu gönderim**: Node tek olayı üst seviyede gönderir; burada `{"events":[...]}` ile en
  çok 8 olay. Hub'ın `release` ve bağlam alanlarını olay başına okuduğu için kimlik alanları
  hem üst seviyede (canlılık damgası) hem her olayda.
- **Rota deseni** `RouteEndpoint.RoutePattern.RawText`; klasik MVC rotasında
  `{controller}`/`{action}` gerçek adlarla değiştirilir.

---

## Sessiz arıza karşıtı

Bu ailenin tekrarlayan hatası **sessizce çalışmama**. Hub geçersiz isteğe de `204` döner.

- `--vitalwatch-status [--test] [--vitalwatch] [--deps]` — yereldeki yanlış yapılandırmayı gösterir;
  `--test` sonucu okur, "gönderildi" varsaymaz.
- Eksik/hatalı yapılandırmada başlangıçta **tek** uyarı (`VitalWatch` kategorisi).
- Testler sahte hub'ın **kabul ettiğini** doğrular; "204 aldım" yeterli sayılmaz.
- Her istekte `sdk_version` — panel eski sürümde kalan projeyi gösterir.
