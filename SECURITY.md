# Güvenlik politikası

NetGadget yönetici izniyle çalışır ve bilgisayarınızın ağ trafiğini izler. Bu yüzden güvenlik açıklarını ciddiye alıyorum.

## Desteklenen sürümler

| Sürüm | Destek |
|---|---|
| En son sürüm ([Releases](https://github.com/kubikanber/netgadget/releases/latest)) | ✅ |
| Daha eski sürümler | ❌ — lütfen en son sürüme güncelleyin |

## Güvenlik açığı bildirme

**Güvenlik açıklarını herkese açık issue olarak açmayın.**

Bunun yerine GitHub'ın gizli bildirim özelliğini kullanın:

👉 **[Güvenlik açığı bildir](https://github.com/kubikanber/netgadget/security/advisories/new)**
(depo sayfasında **Security → Report a vulnerability**)

Bildiriminizde mümkünse şunlar olsun:

- Etkilenen sürüm ve Windows sürümü
- Açığın ne olduğu ve olası etkisi
- Tekrarlama adımları ya da kavram kanıtı
- Varsa önerdiğiniz çözüm

Bu, gönüllü olarak sürdürülen küçük bir proje. Bildirimlere makul en kısa sürede dönüş yapmaya, doğrulanan açıkları
düzeltip yeni bir sürümle yayımlamaya çalışırım. Düzeltme yayımlanana kadar ayrıntıları gizli tutmanızı rica ederim;
isterseniz güvenlik bülteninde adınızla teşekkür edilir.

## Kapsam

Örnek olarak kapsam içinde olanlar:

- Gadget'ın yönetici yetkisiyle istenmeyen kod çalıştırmasına yol açan durumlar
  (ör. `%APPDATA%\NetGadget` altındaki dosyalar ya da izlenen trafik üzerinden)
- Gadget'ın açıklanandan farklı olarak veri göndermesi ya da kaydetmesi
- İzlenen trafikten gelen verilerin (alan adları, komut satırları) arayüzde ya da kopyalanan raporlarda istismar edilebilmesi
- Şüpheli bağlantı incelemesinin kolayca ve sistematik olarak atlatılması

Kapsam dışında olanlar:

- Bir zararlı yazılımın şüphe puanını tek bir durumda düşük alması (bu bir özellik önerisidir — issue açabilirsiniz)
- Bilgisayarda zaten yönetici yetkisi olan bir saldırganın yapabildikleri
- Windows'un, PowerShell'in ya da .NET Framework'ün kendi açıkları

## Tasarım ilkeleri

- Gadget, izlediği verileri bilgisayarın dışına **göndermez**.
- Kendi ürettiği tek ağ trafiği, alan adı bilinmeyen internet IP'leri için DNS ters sorgusudur (menüden kapatılabilir).
- Kalıcı veriler yalnızca `%APPDATA%\NetGadget` klasöründe tutulur.
