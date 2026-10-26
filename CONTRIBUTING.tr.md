# Katkı rehberi

[English](CONTRIBUTING.md) · **Türkçe**

NetGadget'a katkıda bulunmak istediğiniz için teşekkürler! Hata bildirimi, öneri, çeviri, belge düzeltmesi ya da
kod — hepsi değerli.

## Nereden başlamalı?

- **Hata mı buldunuz?** [Hata bildirimi](https://github.com/kubikanber/netgadget/issues/new?template=hata-bildirimi.yml) açın.
- **Bir fikriniz mi var?** Önce README'deki [Yol haritası](README.tr.md#yol-haritası)'na bakın, sonra
  [özellik önerisi](https://github.com/kubikanber/netgadget/issues/new?template=ozellik-onerisi.yml) açın.
- **Kendi dilinizi mi eklemek istiyorsunuz?** [Yeni dil ekleme](#yeni-dil-ekleme) bölümüne bakın.
- **Güvenlik açığı mı?** Issue açmayın; [SECURITY.md](SECURITY.md) dosyasındaki gizli bildirim yolunu kullanın.
- Büyük bir değişiklik yapmayı düşünüyorsanız, emeğiniz boşa gitmesin diye önce bir issue'da konuşalım.

Issue'ları ve pull request'leri **Türkçe ya da İngilizce** yazabilirsiniz.

## Proje yapısı

| Dosya | Görev |
|---|---|
| `Baslat.cmd` | Başlatıcı; PowerShell'i doğru parametrelerle açar |
| `NetGadget.ps1` | WPF arayüz, menü, ayarlar, dil yükleme, yönetici izni isteği |
| `NetSampler.cs` | ETW (Kernel-Network, DNS-Client) oturumu, TCP tabloları, EStats, alan adı çözümleme, uygulama gruplama; `Loc` çeviri sınıfı |
| `Inspector.cs` | Dijital imza, süreç bilgisi (WMI), şüphe puanı kuralları, "ilk kez görüldü" ve güvenilir listesi |
| `lang/*.json` | Arayüz metinleri (anahtar → çeviri) |
| `tools/Test-Translations.ps1` | Dil dosyaları denetimi |
| `README.md`, `README.tr.md` | Belgeler (İngilizce ana sürüm + çeviriler) |
| `docs/images/<dil>/` | README ekran görüntüleri, dil başına bir klasör |

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

### Arayüz metinleri koda yazılmaz

Kullanıcının gördüğü her metin bir dil anahtarıyla gelir:

- PowerShell: `T 'menu.sort'`, değişkenli metinlerde `T 'footer.apps' $sayi`
- C#: `Loc.T("reason.temp")`, `Loc.T("reason.lookalike", exe, ad)`

Yeni anahtar adları küçük harf ve noktalı olmalıdır (`alan.ad`, ör. `detail.parent`). Anahtarı **tüm**
`lang\*.json` dosyalarına ekleyin; bilmediğiniz dillere İngilizcesini yazabilirsiniz.

### Dosya kodlaması

- `NetGadget.ps1` ve `tools\*.ps1` **BOM'lu UTF-8** olmalıdır. BOM yoksa PowerShell 5.1 dosyayı ANSI olarak okur ve
  ASCII dışındaki karakterler bozulur.
- `lang\*.json` dosyaları UTF-8'dir.
- Satır sonları `.gitattributes` ile yönetilir; elle değiştirmeyin.

### Gizlilik ilkesi

- Gadget izlediği veriyi **hiçbir yere göndermez**. İnternete bağlanan bir özellik (ör. itibar sorgusu) eklenecekse
  varsayılan olarak **kapalı** olmalı ve menüden açıkça etkinleştirilmelidir.
- Gadget'ın kendisi ağ trafiği üretmemeli; üretiyorsa (ters DNS gibi) bu belgelenmeli ve kapatılabilmelidir.

### Şüphe puanı kuralları

Yeni kurallar `Inspector.cs` içindeki `Evaluate` metoduna eklenir. Her kural:

- Tek başına **yanlış alarm üretmeyecek** kadar seçici olmalı ya da düşük puanlı olmalı,
- Kullanıcıya **neden** işaretlendiğini anlatan bir gerekçe yazmalı: `Reason(app, puan, Loc.T("reason.newrule"))`
  ve anahtar tüm dil dosyalarına eklenmeli,
- Tüm README'lerdeki (`README.md`, `README.tr.md`) puan tablosuna eklenmelidir.

## Yeni dil ekleme

Arayüzdeki tüm metinler `lang\<dil kodu>.json` dosyalarındadır.

1. `lang\en.json` dosyasını kopyalayıp iki harfli dil koduyla adlandırın (ör. `lang\de.json`).
2. `language.name` değerini dilin kendi dilindeki adıyla (`Deutsch`), `language.culture` değerini kültür koduyla
   (`de-DE`) değiştirin. Kültür kodu sayı ve tarih biçimini belirler.
3. Diğer değerleri çevirin. **Anahtarlara dokunmayın.** `{0}`, `{1}` yer tutucularını aynen koruyun; sıraları
   cümleye göre değişebilir.
4. Denetimi çalıştırın; eksik, fazla ya da yer tutucusu farklı anahtar varsa listeler:

   ```powershell
   .\tools\Test-Translations.ps1
   ```

5. Gadget'ı yeni dilde deneyin ve taşan metin olup olmadığına bakın:

   ```powershell
   .\NetGadget.ps1 -Snapshot C:\Temp\de.png -Language de
   ```

Yeni dil, gadget'ın **Dil / Language** menüsünde kendiliğinden görünür; Windows dili o dilse otomatik seçilir.
Bir anahtar çevrilmemişse İngilizcesi gösterilir, bu yüzden kısmi çeviriler de işe yarar.

## Belge çevirileri

Belgelerde **İngilizce ana sürümdür**; çeviriler aynı adı dil koduyla taşır:

| Belge | Diller |
|---|---|
| `README.md` | İngilizce · [`README.tr.md`](README.tr.md) Türkçe |
| `CONTRIBUTING.md` | İngilizce · [`CONTRIBUTING.tr.md`](CONTRIBUTING.tr.md) Türkçe |
| `SECURITY.md`, `CHANGELOG.md` | Şimdilik yalnızca Türkçe — İngilizce çevirilere açığız |

- Yeni bir çeviri `<AD>.<dil kodu>.md` olarak eklenir (ör. `README.de.md`).
- Her sürümün en üstünde dil seçici satırı vardır (`[English](README.md) · **Türkçe**`). Yeni dil eklediğinizde
  bu satırı **tüm** dil sürümlerinde güncelleyin.
- Ekran görüntüleri `docs/images/<dil kodu>/` klasörüne konur ve README'nin o dildeki sürümünden bağlanır.
- Bir belgeyi değiştirirken diğer dil sürümlerini de güncelleyin. Çeviremiyorsanız pull request açıklamasına
  "çeviri bekliyor" yazın; başkası tamamlayabilir.

## Test etme

Gadget'ı ekranda göstermeden, ekran dışında çizip PNG'ye kaydeden bir test modu var:

```powershell
.\NetGadget.ps1 -Snapshot C:\Temp\deneme.png
.\NetGadget.ps1 -Snapshot C:\Temp\deneme-en.png -Language en
```

- `-Snapshot`, yönetici izni istemez ve `known.tsv` / `trusted.tsv` dosyalarınıza dokunmaz.
- `-Language` ayarlardaki dili geçersiz kılar (`tr`, `en`, `auto`).
- `-NoElevate` ile gadget'ı yönetici izni istemeden (sınırlı modda) açabilirsiniz.
- ETW ile ilgili değişiklikleri mutlaka **yönetici olarak** da deneyin; sağ üstte `canlı · TCP + UDP`
  (`live · TCP + UDP`) görünmelidir.

Pull request açmadan önce:

- [ ] Gadget hem yönetici olarak hem `-NoElevate` ile hatasız açılıyor
- [ ] `-Snapshot` görüntüsü en az iki dilde (`-Language tr`, `-Language en`) düzgün
- [ ] Arayüzde yeni metin varsa tüm `lang\*.json` dosyalarında var ve `tools\Test-Translations.ps1` temiz
- [ ] Belge değiştiyse tüm dil sürümleri güncellendi (ya da "çeviri bekliyor" notu var)
- [ ] Gerekiyorsa `CHANGELOG.md` güncellendi

## Commit ve pull request

- Commit mesajları Türkçe ya da İngilizce olabilir; ilk satır kısa ve ne yapıldığını anlatan bir özet olsun.
- Her pull request tek bir konuya odaklansın.
- Katkılarınız projenin [MIT lisansı](LICENSE) altında yayımlanır.
