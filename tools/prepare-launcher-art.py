"""Cut the DiRT3VR badge out of its black artwork background.

Usage: python tools/prepare-launcher-art.py <artwork.png>
Writes launcher/assets/DiRT3VR.png with a transparent background and a thin black
keyline, like the DiRT2VR and GRID2VR artwork. Then run tools/make-launcher-icon.ps1.
"""
from pathlib import Path
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter


def grow(mask, pixels):
    """Round dilation: a Gaussian blur thresholded at 2% reaches ~2.05 sigma."""
    return mask.filter(ImageFilter.GaussianBlur(pixels / 2.05)).point(lambda v: 255 if v > 5 else 0)


def shrink(mask, pixels):
    return ImageChops.invert(grow(ImageChops.invert(mask), pixels))


def main(source):
    art = Image.open(source).convert('RGB')
    # Artwork: anything clearly brighter than the black background.
    ink = art.convert('HSV').getchannel('V').point(lambda v: 255 if v > 56 else 0)
    # Close small gaps in the rim so the dark shield field is enclosed.
    closed = shrink(grow(ink, 14), 14)
    # Everything the border can reach through background is outside the badge.
    outside = ImageChops.invert(closed)
    outside = outside.point(lambda v: 128 if v else 0)
    for corner in [(0, 0), (art.width - 1, 0), (0, art.height - 1), (art.width - 1, art.height - 1)]:
        ImageDraw.floodfill(outside, corner, 255)
    silhouette = outside.point(lambda v: 0 if v == 255 else 255)
    # Keep a black keyline around the artwork, then soften the cut edge.
    alpha = grow(silhouette, 7).filter(ImageFilter.GaussianBlur(1.2))
    badge = art.convert('RGBA')
    badge.putalpha(alpha)
    target = Path(__file__).resolve().parents[1] / 'launcher' / 'assets' / 'DiRT3VR.png'
    target.parent.mkdir(exist_ok=True)
    badge.save(target)
    print(target, f'{sum(1 for v in alpha.getdata() if v < 16)} transparent pixels')


if __name__ == '__main__':
    main(Path(sys.argv[1]))
