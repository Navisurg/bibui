# Bibui.Imaging

Pictures beyond PNG, for a bibui app that is handed a photo or a web image.
It decodes JPEG through the system's libjpeg-turbo (`libturbojpeg.so.0`) and
WebP through its libwebp (`libwebp.so.7`) to `Bibui.Core`'s `RgbaImage`, the
straight 8-bit RGBA that `Png.Decode` gives and an `RGBA8` texture takes.

```csharp
using Bibui.Core.Imaging;   // RgbaImage, Png
using Bibui.Imaging;        // Images, Jpeg, WebP

RgbaImage? image = Images.Decode(bytes);   // PNG, JPEG or WebP, by its first bytes; null if not
```

- `Jpeg.Decode`: baseline and progressive, every subsampling, grey and Adobe
  CMYK, turned upright by the EXIF orientation. A file cut short in its scan
  decodes as far as it goes.
- `WebP.Decode`: lossy, lossless and either with alpha. An animated WebP is
  refused.
- `Images.Decode` and `Images.DecodeFile` answer null for anything they cannot
  decode, a missing library included, and never throw for what is in the
  bytes; the format's own class throws `JpegException` or `WebPException` to
  say why.

The libraries are loaded by hand the first time a picture of their kind is
decoded, not imported: the package links nothing, and without them an app
still starts, PNG still decodes, and `Jpeg.IsAvailable` or `WebP.IsAvailable`
is false. No colour management is applied. Native AOT clean.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
