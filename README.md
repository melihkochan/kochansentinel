# KOCHAN Sentinel

Windows 11 (.NET 8 / WPF) için geliştirilmiş sistem telemetrisi, donanım benchmark'ı, TreeSize/Treemap disk analizi, oyun optimizasyonu ve aktif savunma (user-mode EDR) merkezi.

## Doğrudan İndir (Kurulumsuz Tek EXE)

- **Doğrudan İndirme:** [KochanSentinel.exe (Son Sürüm)](https://github.com/melihkochan/kochansentinel/releases/latest/download/KochanSentinel.exe)
- **Tüm Sürümler & Sürüm Notları:** [GitHub Releases](https://github.com/melihkochan/kochansentinel/releases/latest)

> **Not:** Tek dosya (Self-Contained Win-x64) olarak paketlenir; ekstra `.NET Runtime` kurulumu gerekmez. Tam sistem optimizasyonu ve güvenlik denetimi için yönetici yetkisiyle çalışır.

## Öne Çıkan Özellikler

- **Canlı Sistem İzleme & Mini HUD (`Alt + Z`)**
  - Gerçek zamanlı CPU, RAM, GPU, VRAM, sıcaklık, fan RPM, okuma/yazma, ağ hızı, Ping/Jitter/paket kaybı ve SSD S.M.A.R.T. sağlığı (LibreHardwareMonitor tabanlı).
  - Kayar Mini HUD üzerinde özelleştirilebilir metrik görünürlüğü ve saydamlık ayarı.
- **Canlı Monitör & Ağ Kalkanı**
  - Süreç bazlı EcoQoS, askıya alma/devam, dijital imza doğrulaması ve Firewall outbound engelleme/açma.
  - IPv4/IPv6 aktif bağlantı haritası + GeoIP + riskli C2 port analizi ve DNS çözümleyici önbellek inceleyici.
- **Akıllı Temizlik, RAM/ISLC & Windows Tweaks**
  - Tarayıcı/sistem önbellek temizliği, RAM Working Set + Standby List optimizasyonu ve otomatik ISLC koruyucusu.
  - Tek tıkla geri alınabilir Oyun/Gizlilik Registry tweaks + canlı `SFC /scannow` ve `DISM /RestoreHealth` onarım konsolu.
- **Disk Analizi (TreeSize & Treemap) & Sistem Benchmark**
  - Klasör ağacı, interaktif Treemap blok haritası, en büyük dosyalar listesi ve geri dönüşüme gönderme kilidi.
  - Gerçek donanım benchmark testleri (`SHA-256` CPU, RAM bant genişliği/gecikme, `FILE_FLAG_NO_BUFFERING` NVMe/SSD 4K IOPS).
- **Güvenlik, Karantina & Aktif Savunma**
  - Gerçek zamanlı dosya kalkanı (SHA-256, Shannon Entropi, imza doğrulama, VirusTotal/MalwareBazaar, Defender).
  - DPAPI/AES şifreli Karantina Kasası, Başlangıç/Görev/Servis/WMI kalıcılık denetimi, BYOVD `.sys` çekirdek sürücü taraması, enjekte DLL/tarayıcı eklentisi denetimi, Fidye Honeypot tuzakları, USB bellek tarayıcısı, işlem öncesi otomatik Geri Yükleme Noktası ve `--watchdog` Self-Defense.

## Kaynaktan Derleme ve Paketleme

```powershell
dotnet build KochanSentinel.sln -c Release
```

Tek dosya (Standalone `KochanSentinel.exe`) üretmek için:

```powershell
.\publish_singlefile.ps1
```
