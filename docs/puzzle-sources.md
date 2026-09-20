# Puzzle kaynak politikası

## Manifestler

- `puzzles/test-vectors.json`: küçük, deterministik ve bilinen sonuçlu geliştirme vektörleri.
- `puzzles/public-btc-puzzles.json`: doğrulanmış Bitcoin Puzzle 1–160 hedefleri.

Her iki dosya da build sırasında `TrPuzzle.Core` içine gömülür. Çalışma zamanında değiştirilmiş bir
yan dosya uygulamanın hedef kümesini değiştiremez.

## Kamu hedefi ekleme kontrol listesi

Bir `public-puzzle` girdisi eklenmeden önce:

1. Hedefin kamuya açık bir challenge/puzzle olduğu en az iki bağımsız kaynakla doğrulanır.
2. Orijinal duyuru veya birincil kaynak URL'si `source` alanına yazılır.
3. Ağ, sıkıştırılmış/sıkıştırılmamış public key varsayımı ve inclusive range sınırları doğrulanır.
4. `keyRangeStart` ve `keyRangeEnd` tam 64 hex karakter, `targetHash160` tam 40 hex karakter olur.
5. Puzzle'ın solved/unsolved durumu ekleme anında tarihli olarak belgelenir.
6. Manifest değişikliği code review ve test gerektirir.

Durumu veya kaynağı doğrulanmamış hedef eklenmez. Kullanıcı tarafından sağlanan hedefin manifeste
çalışma zamanında eklenmesi desteklenmez.

## Kamu girdileri

Manifestteki `btc-puzzle-1` … `btc-puzzle-160` girdileri, kamuya açık challenge aralıkları ve
adres HASH160 değerleridir. Aralık ve adres verileri kaynak snapshot'ından alınmış, HASH160
değerleri Base58Check payload'ından yeniden çıkarılmıştır. Çözülmemiş hedefler için başarı veya
ödül varsayımı yapılmaz; durum bilgisi yalnızca manifestin üretildiği kaynak snapshot'ını gösterir.

Manifest üretiminde karşılaştırılan kaynaklar: [BTC Puzzle Search](https://btcpuzzlesearch.com/)
ve [Needlespace puzzle listesi](https://needlespace.com/en/puzzle). Veri snapshot'ı olarak
[roadhero/Bitcoin-Puzzle-Info](https://github.com/roadhero/Bitcoin-Puzzle-Info/blob/main/BTC-Solved-Unsolved.txt)
kullanılmıştır. Bu URL'ler çalışma zamanında çağrılmaz; manifest derleme içine gömülür.

Bir kaynağın çevrimiçi durumunun değişmesi çalışma zamanında manifesti değiştirmez. Güncelleme,
iki bağımsız kaynak karşılaştırması ve test çalıştırılması sonrasında yeni bir manifest değişikliği
olarak yapılır.
