# RResizer — automatic image optimization

This is a separate development version based on the release project.

## Behavior

- `Оптимизировать изображения` is enabled by default.
- The user does not choose a quality value or compression format.
- JPEG and WebP are tested at several internal quality levels. The program keeps the smallest candidate that passes an internal visual-similarity check.
- PNG is saved with maximum PNG compression; when optimization alone would make a PNG larger, the original bytes are retained.
- EXIF, IPTC and XMP metadata are removed. The ICC color profile is intentionally preserved.
- Resize behavior remains the existing fit-inside-target behavior with Lanczos3 and the existing no-upscale option.
- If width and height are both empty while optimization is enabled, the image is not resized; it is only optimized.
- If optimization produces no worthwhile smaller result for an image that was not resized, the original file is copied unchanged.
- Final status reports the total byte reduction when optimization is enabled.

## Important

The automatic visual-quality check is deliberately conservative. It uses sampled RGB error plus a luminance SSIM-style structural similarity score. This is an internal heuristic, not a user-facing quality setting.
