using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices;

namespace IPFS.Services;

/// <summary>
/// 图片预览服务：负责从 IPFS 取图、缩放编码，并在 UI 线程创建 Avalonia 位图。
/// 解码与缩放放在后台线程，避免阻塞界面。
/// </summary>
public sealed class ImagePreviewService
{
    /// <summary>
    /// 将 ImageSharp 图片缩放到指定尺寸内，并直接转换为 Avalonia WriteableBitmap。
    /// 全程不经过 PNG 编解码，像素数据通过 Bgra32 直传。
    /// </summary>
    public static async Task<Bitmap?> ToWriteableBitmapAsync(Image image, int maxWidth, int maxHeight, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        // ① 后台线程：Resize + 提取像素（CPU 密集部分完全离开 UI 线程）
        var (pixels, width, height) = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            // 使用 Bgra32，与 Avalonia 的 PixelFormat.Bgra8888 直接兼容，避免通道互换
            using var clone = image.CloneAs<Bgra32>();

            clone.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(maxWidth, maxHeight),
                Mode = ResizeMode.Max
            }));

            int w = clone.Width;
            int h = clone.Height;
            var buffer = new byte[w * h * 4];
            clone.CopyPixelDataTo(buffer);

            return (buffer, w, h);
        }, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        // ② UI 线程：仅创建位图并拷贝像素（一次内存拷贝，极快）
        return await Dispatcher.UIThread.InvokeAsync<Bitmap?>(() =>
        {
            token.ThrowIfCancellationRequested();

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul); // Bgra32 是非预乘 Alpha

            using (var fb = bitmap.Lock())
            {
                // 逐行拷贝，处理 stride 可能大于 width*4 的情况
                int srcStride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(
                        pixels, y * srcStride,
                        fb.Address + y * fb.RowBytes,
                        srcStride);
                }
            }
            return bitmap;
        }, DispatcherPriority.Render);
    }
    /// <summary>按 CID 加载图片，并缩放到指定尺寸内</summary>
    public static async Task<Bitmap?> LoadAsync(string cid, int maxWidth, int maxHeight, CancellationToken token = default)
    {
        Image? image = null;
        try
        {
            await Task.Run(async () =>
            {
                using var stream = await IPFSHttpAPI.DownloadAsync(IPFSHttpAPI.BuildCommand("cat", cid), token).ConfigureAwait(false);
                image = await Image.LoadAsync(stream, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            if (image is null) return null;//Image.LoadAsync 不返回 null，此检查用于满足可空流分析
            return await ToWriteableBitmapAsync(image, maxWidth, maxHeight, token).ConfigureAwait(false);
        }
        finally
        {
            image?.Dispose();
        }
    }
    /// <summary>加载专辑封面（文件名主名为 Cover 的文件）</summary>
    public static Task<Bitmap?> LoadCoverAsync(Album album, int maxWidth, int maxHeight, CancellationToken token = default)
    {
        var cover = album.Files?.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file.Name), "Cover", StringComparison.OrdinalIgnoreCase));
        return cover == null ? Task.FromResult<Bitmap?>(null) : LoadAsync(cover.Cid, maxWidth, maxHeight, token);
    }

    /// <summary>把已解码的 ImageSharp 图片转换为 Avalonia 位图(弃用)</summary>
    public static async Task<Bitmap?> FromImageAsync(Image image, int maxWidth, int maxHeight, CancellationToken token = default)
    {
        byte[] png = await Task.Run(() => Encode(image, maxWidth, maxHeight), token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        return await ToBitmapAsync(png);
    }
    
    static byte[] Encode(Image image, int maxWidth, int maxHeight)
    {
        using var clone = image.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(maxWidth, maxHeight),
            Mode = ResizeMode.Max
        }));
        using var stream = new MemoryStream();
        clone.SaveAsPng(stream);
        return stream.ToArray();
    }

    static async Task<Bitmap?> ToBitmapAsync(byte[] png)
        => await Dispatcher.UIThread.InvokeAsync<Bitmap?>(() =>
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        });
}
