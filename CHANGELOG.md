# Değişiklik günlüğü

Bu projedeki önemli değişiklikler bu dosyada listelenir.
Biçim [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/) önerisine, sürüm numaraları
[Anlamsal Sürümleme](https://semver.org/lang/tr/) kurallarına dayanır.

## [Yayımlanmadı]

### Eklenenler

- İngilizce katkı rehberi: `CONTRIBUTING.md` İngilizce, Türkçesi `CONTRIBUTING.tr.md` oldu
- Katkı rehberine "Belge çevirileri" bölümü (adlandırma, dil seçici satırı, ekran görüntüsü klasörleri) ve
  arayüz metinleri / anahtar adlandırma kuralları eklendi

## [1.1.0] - 2026-10-26

Çoklu dil desteği.

### Eklenenler

- **Çoklu dil desteği:** İngilizce ve Türkçe arayüz. Metinler `lang\*.json` dosyalarında; dil Windows'un
  arayüz diline göre otomatik seçilir, sağ tık menüsündeki **Dil / Language** ile gadget kapanmadan değiştirilir.
  Sayı ve tarih biçimi seçilen dile uyar. Test için `-Language` parametresi.
- `tools\Test-Translations.ps1`: dil dosyalarında eksik, fazla ya da yer tutucusu farklı anahtarları bulur
- İngilizce `README.md`; Türkçe belge `README.tr.md` oldu. Ekran görüntüleri iki dilde (`docs/images/en`, `docs/images/tr`)
- Yeniden yazılmış README: tanıtım, kurulum, kullanım rehberi, şüphe puanı tablosu, gizlilik, mimari şeması,
  sorun giderme ve yol haritası; örnek verilerle hazırlanmış ekran görüntüleri
- Hata bildirimi ve özellik önerisi için issue şablonları
- `SECURITY.md` güvenlik politikası ve gizli güvenlik açığı bildirimi
- `CONTRIBUTING.md` katkı rehberi ve bu değişiklik günlüğü
- `.gitattributes`: Windows dosyalarının satır sonlarını sabitler

### Değişenler

- Dil ayarı "Otomatik" iken gadget, Windows'un arayüz dili Türkçe değilse **İngilizce** açılır
  (önceki sürüm her zaman Türkçeydi). Menüden Türkçe seçilebilir.

## [1.0.0] - 2026-10-26

İlk sürüm.

### Eklenenler

- Canlı toplam indirme / yükleme hızı ve son 60 saniyenin grafiği
- ETW (`Microsoft-Windows-Kernel-Network`) ile uygulama başına TCP + UDP, IPv4 + IPv6 trafiği;
  ETW kullanılamazsa TCP EStats'a, yönetici izni yoksa TCP bağlantı tablosuna geri düşme
- Uygulama ayrıntı paneli: uç noktalar, protokol, hizmet / port adı, hız ve oturum toplamı
- DNS istemcisi olaylarından alan adları; yalnızca DNS ile ters sorgu; isteğe bağlı yerel cihaz adları
- Yerel ağ keşfi trafiğinin (mDNS, SSDP, LLMNR, NetBIOS…) tek satırda gruplanması
- svchost süreçlerinin barındırdıkları Windows hizmetinin adıyla gösterilmesi
- Şüpheli bağlantı incelemesi: dijital imza, çalıştığı klasör, sahte / benzer sistem dosyası adları,
  komut satırı ve başlatan süreç, DNS'siz IP, şüpheli portlar, gelen bağlantılar ve dinlenen portlar
- Şüphe puanı ve rozetler (`şüpheli`, `dikkat`, `yeni`, `güvenilir`), puan gerekçeleri
- "İlk kez görüldü" tespiti (5 dakikalık öğrenme modu) ve güvenilir listesi (uygulama ve uç nokta)
- Sıralama: anlık hız, oturum toplamı, bağlantı sayısı, ad, şüphe puanı
- Kenarlıksız, yarı saydam, sürüklenebilir pencere; tepsi simgesi ve menü; ayarların hatırlanması

[Yayımlanmadı]: https://github.com/kubikanber/netgadget/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/kubikanber/netgadget/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/kubikanber/netgadget/releases/tag/v1.0.0
