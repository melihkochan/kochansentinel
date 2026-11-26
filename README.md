# KOCHAN Sentinel

Windows 10/11 (.NET 8 / WPF) için geliştirilmiş sistem telemetrisi, donanım & termal benchmark, paralel TreeSize/Treemap disk analizi + yinelenen dosya bulucu, oyun optimizasyonu, tek tıkla otomatik güncelleyici ve aktif savunma (user-mode EDR) merkezi.

## Doğrudan İndir & Otomatik Güncelleme (Kurulumsuz Tek EXE)

- **Doğrudan İndirme:** [KochanSentinel.exe (Son Sürüm)](https://github.com/melihkochan/kochansentinel/releases/latest/download/KochanSentinel.exe)
- **SHA-256 Doğrulama Dosyası:** [SHA256SUMS.txt](https://github.com/melihkochan/kochansentinel/releases/latest/download/SHA256SUMS.txt)
- **Tüm Sürümler & Sürüm Notları:** [GitHub Releases](https://github.com/melihkochan/kochansentinel/releases/latest)

> **Otomatik Güncelleme:** Bir kez indirdikten sonra yeni sürümlerde tekrar indirmenize gerek yoktur. Uygulama açılışta GitHub Releases üzerinden yeni sürümü sessizce denetler; tek tıkla **SHA-256 doğrulamalı otomatik güncelleme** yapar ve kendini yeniden başlatır.
>
> **Not:** Tek dosya (Self-Contained Win-x64) olarak paketlenir; ekstra `.NET Runtime` kurulumu gerekmez. Tam sistem optimizasyonu ve güvenlik denetimi için yönetici yetkisiyle çalışır.

## Öne Çıkan Özellikler

- **Canlı Sistem İzleme, Komut Paleti (`Ctrl + K`) & Mini HUD (`Ctrl + Shift + H`)**
  - Gerçek zamanlı CPU, RAM, GPU, VRAM, sıcaklık, fan RPM, okuma/yazma, ağ hızı, Ping/Jitter/paket kaybı, Boot süresi ve SSD S.M.A.R.T. sağlığı (Defender dostu **Sürücüsüz Güvenli Sensör Modu** seçeneğiyle).
  - Oyun içi **Click-Through (tıklama geçirgenliği)**, kompakt mod, özelleştirilebilir kısayol (`Ctrl+Shift+H` / `Alt+Z` / `Ctrl+Alt+H` / `F9`) ve Windows 11 Mica/Acrylic pencere efekti.
  - Tek tıkla modern **HTML Sistem Sağlık & Güvenlik Raporu** dışa aktarma.
- **Canlı Monitör, Süreç Bazlı Ağ Hızı & Güvenli DNS (DoH)**
  - ETW `Kernel-Network` tabanlı süreç bazlı anlık indirme/yükleme hızı (`KB/s` / `MB/s`), sağ tık bağlam menüsü (Öncelik, EcoQoS, Askıya Al, EDR Tara, Firewall Engel) ve PID korumalı canlı yenileme.
  - IPv4/IPv6 aktif bağlantı haritası + HTTPS GeoIP + riskli C2 port analizi, DNS önbelleği ve tek tıkla **Cloudflare / AdGuard / Quad9 / Google DoH DNS Değiştirici**.
- **Akıllı Temizlik, RAM/ISLC, Tweaks & Başlangıç/Boot Yöneticisi**
  - Tarayıcı/sistem önbellek temizliği, RAM Working Set + Standby List optimizasyonu ve otomatik ISLC koruyucusu.
  - Windows açılış süresi (`BootTime`) analizi ve Görev Yöneticisi uyumlu, geri alınabilir **Başlangıç Yöneticisi** (`StartupApproved`).
  - Tek tıkla geri alınabilir Oyun/Gizlilik Registry tweaks + canlı `SFC /scannow` ve `DISM /RestoreHealth` onarım konsolu.
- **Paralel Disk Analizi (TreeSize & Treemap), Duplicate Finder & Termal Benchmark**
  - Çok çekirdekli paralel klasör taraması, interaktif Treemap, en büyük dosyalar ve 3 aşamalı **SHA-256 Yinelenen Dosya Bulucu**.
  - Gerçek donanım benchmark testleri (`SHA-256` CPU, RAM bant genişliği/gecikme, 3D SIMD/Vektör hesaplama, `FILE_FLAG_NO_BUFFERING` NVMe 4K IOPS) + canlı **Termal Throttling Tespiti**.
- **Ekran, Sürücü, Servis & GitHub Hızlandırma Stüdyosu**
  - Bağlı monitörlerde yenileme hızı (Hz) uyarısı + tek tıkla maksimum Hz'e geçiş, DDC/CI donanımsal parlaklık/kontrast, anlık geri alınabilir renk/gama profilleri (FPS Netlik, sRGB Tasarım, Sinema, Gece Göz Koruma) ve ICC/GPU renk önerileri.
  - Aygıt Yöneticisi eksik/hatalı donanım denetimi, eski sürücü tespiti ve resmi Windows Update WHQL sürücü kurulumu.
  - Kullanılmayan telemetri/arka plan Windows servisleri için güvenli tek tıkla optimizasyon + arka plan gereksiz/riskli süreç denetimi.
  - `WinUtil`, `Win11Debloat` ve `Sophia Script` kaynaklı güvenli, geri alınabilir Windows 11 hızlandırma tweaks paketi.
- **Güvenlik, Akışlı Karantina & Aktif Savunma**
  - Gerçek zamanlı dosya kalkanı (SHA-256, Shannon Entropi, imza doğrulama, VirusTotal/MalwareBazaar Auth-Key, Defender) ve DPAPI şifreli API/Webhook saklama.
  - Büyük dosyaları belleği tüketmeden şifreleyen akışlı (chunked) **AES-256-GCM + DPAPI Karantina Kasası**.
  - **Kripto Pano Koruması (Clipper Guard)**, delta-I/O tabanlı güvenli **Fidye Honeypot**, BYOVD `.sys` çekirdek sürücü taraması, enjekte DLL/tarayıcı eklentisi denetimi, USB bellek tarayıcısı, opsiyonel **Discord Webhook** kritik tehdit bildirimleri ve `--watchdog` Self-Defense.

## Kaynaktan Derleme ve Paketleme

```powershell
dotnet build KochanSentinel.sln -c Release
```

Tek dosya (Standalone `KochanSentinel.exe` + `SHA256SUMS.txt`) üretmek için:

```powershell
.\publish_singlefile.ps1
```
