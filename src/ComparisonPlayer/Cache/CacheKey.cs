using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ComparisonPlayer.Cache;

/// <summary>
/// Ключ записи кэша: по нему прокси переиспользуется между запусками.
/// Считать хэш всего файла нельзя — 4K-ролики весят гигабайты, а ключ нужен
/// до открытия. Поэтому берём длину, время изменения и по мегабайту с начала
/// и с конца: этого достаточно, чтобы отличить два разных ролика и заметить
/// перезапись файла тем же именем.
/// </summary>
public static class CacheKey
{
    private const int SampleSize = 1 << 20;

    /// <param name="filePath">Исходный ролик.</param>
    /// <param name="parameters">
    /// Подпись параметров прокси: при их смене ключ меняется, и старый кэш
    /// перестаёт подходить вместо того, чтобы молча использоваться.
    /// </param>
    public static string For(string filePath, string parameters)
    {
        var info = new FileInfo(filePath);

        using var sha = SHA256.Create();
        var header = Encoding.UTF8.GetBytes(
            $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{parameters}|");
        sha.TransformBlock(header, 0, header.Length, null, 0);

        using (var stream = File.OpenRead(filePath))
        {
            var buffer = new byte[SampleSize];

            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            sha.TransformBlock(buffer, 0, read, null, 0);

            // Хвост берём, только если он не пересекается с уже прочитанным началом.
            if (info.Length > SampleSize * 2L)
            {
                stream.Seek(-SampleSize, SeekOrigin.End);
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        sha.TransformFinalBlock([], 0, 0);

        // 24 шестнадцатеричных знака (96 бит) — имя папки остаётся коротким,
        // а случайное совпадение ключей практически исключено.
        return Convert.ToHexString(sha.Hash!)[..24].ToLowerInvariant();
    }
}
