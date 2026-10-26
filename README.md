# Ağ Etkinliği — masaüstü gadget'ı

İnternet üzerinden veri alışverişi yapan uygulamaları (TCP + UDP, IPv4 + IPv6) saniyede bir güncelleyerek gösterir.
Ek kurulum gerekmez (Windows 10/11, yerleşik PowerShell 5.1).

## Başlatma

`Baslat.cmd` dosyasına çift tıklayın. Windows yönetici izni (UAC) ister:

| Mod (sağ üstte yazar) | Ne gösterir |
|---|---|
| **canlı · TCP + UDP** (yönetici izniyle) | Uygulama başına ↓/↑ hız; TCP ve UDP uç noktaları, alan adları, oturum toplamları |
| **canlı · yalnızca TCP** | ETW başlatılamazsa yedek: uygulama başına TCP hızları |
| **sınırlı mod** (izin reddedilirse) | Toplam hız, grafik, TCP bağlantısı olan uygulamalar |

Tüm trafik, Windows'un çekirdek ağ olaylarından (ETW, `Microsoft-Windows-Kernel-Network`) okunur; Kaynak İzleyicisi'nin
kullandığı kaynakla aynıdır. Alan adları DNS istemcisi olaylarından, Windows DNS önbelleğinden ve gerekirse ters DNS'ten gelir.

## Kullanım

- **Uygulamaya tıklayın**: ayrıntı paneli açılır — PID'ler, dosya yolu, barındırılan Windows hizmetleri, oturum boyunca
  aktarılan toplam veri ve her uç nokta için protokol (TCP/UDP), alan adı, hizmet/port (HTTPS, QUIC, DNS…), anlık hız.
- **Sürükle**: başlıktan veya grafikten tutup taşıyın (konum hatırlanır).
- **Sağ tık** (veya tepsi simgesi): Göster/Gizle, Her zaman üstte, Yalnızca veri aktaranları göster, Sıralama,
  Tüm ayrıntıları kapat, Ad çözümleme, Kapat.
- **Sıralama** alt menüsü (seçim hatırlanır; geçerli sıralama liste başlığında yazar):
  - *Anlık hız* (varsayılan): o saniye en çok veri aktaran en üstte; liste her saniye yer değiştirebilir.
  - *Oturum toplamı*: gadget açıldığından beri en çok veri aktaran en üstte; satırlarda toplam (Σ) da görünür.
  - *Bağlantı sayısı*: en çok TCP/UDP/yerel ağ ucu olan en üstte.
  - *Ad (A–Z)*: sabit, alfabetik sıra.
- **Ad çözümleme** alt menüsü:
  - *İnternet adresleri için ters DNS* (varsayılan: açık): alan adı bilinmeyen internet IP'leri için yalnızca DNS üzerinden
    PTR sorgusu yapar. Çok küçük bir DNS trafiği üretir ("DNS Client" satırında görünebilir).
  - *Yerel cihaz adlarını çöz (NetBIOS/mDNS)* (varsayılan: kapalı): yerel ağdaki cihazları adlarıyla gösterir
    (ör. `Samandar.local`); bunun için yerel ağa NetBIOS/mDNS sorgusu gönderir ve bu trafik "System" altında görünür.
- Tepsi simgesine çift tık: gizle/göster.
- `svchost` süreçleri, barındırdıkları hizmetin adıyla listelenir (ör. "Windows Push Notifications System Service").

Ayarlar: `%APPDATA%\NetGadget\settings.json`

## Şüpheli bağlantı incelemesi

Her uygulama yerel kontrollerle puanlanır (internete hiçbir şey gönderilmez). Puan ≥ 3 ise satırda **dikkat**, ≥ 5 ise
**şüpheli** rozeti çıkar; ilk kez görülen uygulamalarda **yeni** rozeti görünür. Ayrıntı panelinde nedenler puanlarıyla listelenir.

| İşaret | Puan |
|---|---|
| Sistem dosyası adını taşıyıp farklı konumda çalışmak (ör. `D:\svchost.exe`) | +5 |
| Sistem dosyasına benzeyen ad (`svch0st`, `scvhost`) | +4 |
| Dijital imza geçersiz / imzasız | +4 / +2 |
| Temp, Geri Dönüşüm Kutusu, Public, PerfLogs klasöründen çalışmak | +3–4 |
| İndirilenler, ProgramData kökü veya AppData\Roaming kökünden çalışmak | +2 |
| PowerShell, mshta, rundll32, certutil gibi sistem araçlarının internete bağlanması | +2 |
| Bu araçlarda şüpheli komut satırı (`-enc`, `DownloadString`, `-w hidden`, URL…) | +3 |
| Bu araçların Office/PDF uygulaması tarafından başlatılması | +3 |
| Arka kapı / IRC / Tor / madencilik portları (4444, 6667, 9001, 3333…) | +2 |
| İnternetten gelen bağlantı kabul etmek | +2 |
| DNS sorgusu görülmeden doğrudan IP'ye bağlanmak | +1 |
| İmzası doğrulanmamış programın tüm ağ arayüzlerinde bağlantı beklemesi | +1 |
| İlk kez internete bağlanmak | +1 |

- **Ayrıntı panelinde**: imza ve imzalayan, başlatan süreç, kullanıcı, başlama zamanı, komut satırı, dinlenen portlar;
  *Güvenilir olarak işaretle*, *Gezgin'de göster*, *Bilgileri kopyala* bağlantıları.
- **Uç noktaya sağ tık**: adresi kopyala, *Bu uca güven* (alan adı ya da /24 ağı bazında).
- **Güvenilir** işaretlenen uygulamanın puanı sıfırlanır; işaretler yine görünür ama sayılmaz.
- **İlk kez görüldü**: ilk çalıştırmada 5 dakika boyunca normal trafik öğrenilir; sonra daha önce hiç görülmemiş
  uygulama ve uçlar 10 dakika boyunca *yeni* olarak işaretlenir.
- **Sıralama → Şüphe puanı** ile en riskli uygulamalar en üste alınır.
- Kayıtlar: `%APPDATA%\NetGadget\known.tsv` (görülenler), `trusted.tsv` (güvenilir listesi). Silinirse öğrenme baştan başlar.
- "DNS kaydı yok" işareti yalnızca ETW modunda ve gadget açıldıktan 60 sn sonra kurulan bağlantılar için verilir.
  Tarayıcıların güvenli DNS (DoH) özelliği bu işareti tetikleyebilir; bu yüzden puanı düşüktür.

## Windows açılışında otomatik başlatma

`Win + R` → `shell:startup` → açılan klasöre `Baslat.cmd` için bir kısayol koyun.

## Sınırlamalar

- ICMP (ping) gibi TCP/UDP dışı trafik toplam hıza dahildir ama uygulamalara dağıtılmaz.
- Tarayıcıların "güvenli DNS" (DoH) özelliği açıksa bazı uç noktalar alan adı yerine IP veya ters DNS adıyla görünür.
- Etkinliği biten UDP uçları ve kapanan TCP bağlantıları 30 sn daha listede kalır.
- Yerel (loopback) trafik listelenmez.
