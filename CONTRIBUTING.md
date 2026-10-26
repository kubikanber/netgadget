# Katkı rehberi

NetGadget'a katkıda bulunmak istediğiniz için teşekkürler! Hata bildirimi, öneri, belge düzeltmesi ya da kod —
hepsi değerli.

## Nereden başlamalı?

- **Hata mı buldunuz?** [Hata bildirimi](https://github.com/kubikanber/netgadget/issues/new?template=hata-bildirimi.yml) açın.
- **Bir fikriniz mi var?** Önce README'deki [Yol haritası](README.md#yol-haritası)'na bakın, sonra
  [özellik önerisi](https://github.com/kubikanber/netgadget/issues/new?template=ozellik-onerisi.yml) açın.
- **Güvenlik açığı mı?** Issue açmayın; [SECURITY.md](SECURITY.md) dosyasındaki gizli bildirim yolunu kullanın.
- Büyük bir değişiklik yapmayı düşünüyorsanız, emeğiniz boşa gitmesin diye önce bir issue'da konuşalım.

## Proje yapısı

| Dosya | Görev |
|---|---|
| `Baslat.cmd` | Başlatıcı; PowerShell'i doğru parametrelerle açar |
| `NetGadget.ps1` | WPF arayüz, menü, ayarlar, yönetici izni isteği |
| `NetSampler.cs` | ETW (Kernel-Network, DNS-Client) oturumu, TCP tabloları, EStats, alan adı çözümleme, uygulama gruplama |
| `Inspector.cs` | Dijital imza, süreç bilgisi (WMI), şüphe puanı kuralları, "ilk kez görüldü" ve güvenilir listesi |
| `docs/images/` | README ekran görüntüleri |

C# dosyaları çalışma anında `Add-Type` ile derlenir; ayrı bir derleme adımı ya da bağımlılık yoktur.

## Geliştirme ortamı

- Windows 10 / 11, Windows PowerShell 5.1 (PowerShell 7 **değil** — WPF ve `Add-Type` davranışı 5.1'e göre yazıldı)
- Herhangi bir metin düzenleyici (VS Code önerilir)

## Kurallar

### C# 5 ile sınırlıyız

Windows PowerShell 5.1'in `Add-Type`'ı .NET Framework derleyicisini kullanır ve **C# 5** destekler. Şunları kullanmayın:

- `$"..."` dize yerleştirme → `string.Format` ya da `+`
- `?.` ve `??=` → açık `null` kontrolü
- `nameof`, ifade gövdeli üyeler (`=>`), otomatik özellik başlatıcıları, `out var`, tuple'lar
- LINQ yerine düz döngüler tercih edilir (derlemeye `System.Core` eklemek gerekmesin diye)

### Dosya kodlaması

- `NetGadget.ps1` **BOM'lu UTF-8** olmalıdır. BOM yoksa PowerShell 5.1 dosyayı ANSI olarak okur ve Türkçe karakterler bozulur.
- Satır sonları `.gitattributes` ile yönetilir; elle değiştirmeyin.

### Gizlilik ilkesi

- Gadget izlediği veriyi **hiçbir yere göndermez**. İnternete bağlanan bir özellik (ör. itibar sorgusu) eklenecekse
  varsayılan olarak **kapalı** olmalı ve menüden açıkça etkinleştirilmelidir.
- Gadget'ın kendisi ağ trafiği üretmemeli; üretiyorsa (ters DNS gibi) bu belgelenmeli ve kapatılabilmelidir.

### Şüphe puanı kuralları

Yeni kurallar `Inspector.cs` içindeki `Evaluate` metoduna eklenir. Her kural:

- Tek başına **yanlış alarm üretmeyecek** kadar seçici olmalı ya da düşük puanlı olmalı,
- Kullanıcıya **neden** işaretlendiğini anlatan Türkçe bir gerekçe yazmalı (`Reason(app, puan, "…")`),
- README'deki puan tablosuna eklenmelidir.

## Test etme

Gadget'ı ekranda göstermeden, ekran dışında çizip PNG'ye kaydeden bir test modu var:

```powershell
.\NetGadget.ps1 -Snapshot C:\Temp\deneme.png
```

- `-Snapshot`, yönetici izni istemez ve `known.tsv` / `trusted.tsv` dosyalarınıza dokunmaz.
- `-NoElevate` ile gadget'ı yönetici izni istemeden (sınırlı modda) açabilirsiniz.
- ETW ile ilgili değişiklikleri mutlaka **yönetici olarak** da deneyin; sağ üstte `canlı · TCP + UDP` görünmelidir.

Pull request açmadan önce:

- [ ] Gadget hem yönetici olarak hem `-NoElevate` ile hatasız açılıyor
- [ ] `-Snapshot` görüntüsü düzgün
- [ ] Arayüzde yeni metin varsa Türkçe ve mevcut üslupla uyumlu
- [ ] README ve gerekiyorsa `CHANGELOG.md` güncellendi

## Commit ve pull request

- Commit mesajları Türkçe ya da İngilizce olabilir; ilk satır kısa ve ne yapıldığını anlatan bir özet olsun.
- Her pull request tek bir konuya odaklansın.
- Katkılarınız projenin [MIT lisansı](LICENSE) altında yayımlanır.
