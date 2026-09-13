# TRMachinist

TRMachinist; G-code doğrulama, CNC makine kinematiği, çarpışma analizi ve IPW (işleme sırasındaki stok) simülasyonu için geliştirilen açık kaynak bir **CNC makine simülasyonu ve triple-dexel talaş kaldırma motorudur**.

Proje Windows/.NET 8 üzerinde çalışır ve üç ana bölümden oluşur: simülasyon çekirdeği, WPF/DirectX masaüstü görüntüleyici ve kendi kendine çalışan smoke/regresyon testleri.

> **Alpha sürümdür.** Gerçek tezgâhta güvenlik sertifikasyonu veya NC programı onayı yerine geçmez. Üretim öncesi OEM dokümantasyonu, kontrolör simülasyonu, single-block/dry-run ve işletme güvenlik prosedürleri kullanılmalıdır.

## Ana özellikler

- X/Y/Z dexel ailelerinden oluşan triple-dexel stok modeli
- Kesici profiline göre statik ve swept material-removal sorguları
- Bölgesel IPW yüzey mesh üretimi
- Fanuc ve SINUMERIK odaklı G-code ayrıştırma altyapısı
- Rotary/3+2 kinematik ve koordinat dönüşümleri
- Cutter, shank ve holder parametrik geometrisi
- Mesh collision / oriented bounds altyapısı
- Deterministik oynatma ve regresyon testleri

Derleme ve teknik ayrıntılar için İngilizce [README.md](README.md) ile `docs/` klasörüne bakın.

Bu public repository gerçek müşteri parçalarını, üretim NC dosyalarını, lisanslı Siemens NX bileşenlerini, tezgâh üreticisine ait paketleri veya ticari postprocessor kaynaklarını içermez.

Lisans: **Apache-2.0**.
