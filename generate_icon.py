from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parent


def scaled(value: float, scale: float) -> int:
    return round(value * scale)


def draw_icon(size: int) -> Image.Image:
    # Draw at 4x and downsample once for clean antialiasing at every target size.
    canvas = max(size * 4, 256)
    scale = canvas / 1024
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))

    mask = Image.new("L", image.size, 0)
    mask_draw = ImageDraw.Draw(mask)
    mask_draw.rounded_rectangle(
        (scaled(72, scale), scaled(72, scale), scaled(952, scale), scaled(952, scale)),
        radius=scaled(218, scale),
        fill=255,
    )

    gradient = Image.new("RGBA", image.size)
    gradient_draw = ImageDraw.Draw(gradient)
    top = (22, 139, 255)
    bottom = (7, 86, 201)
    for y in range(canvas):
        t = y / max(canvas - 1, 1)
        color = tuple(round(top[i] * (1 - t) + bottom[i] * t) for i in range(3))
        gradient_draw.line((0, y, canvas, y), fill=(*color, 255))
    image.alpha_composite(Image.composite(gradient, Image.new("RGBA", image.size), mask))

    draw = ImageDraw.Draw(image)
    white = (255, 255, 255, 255)
    blue = (11, 103, 217, 255)
    green = (34, 184, 106, 255)

    draw.rounded_rectangle(
        (scaled(210, scale), scaled(350, scale), scaled(718, scale), scaled(680, scale)),
        radius=scaled(92, scale),
        fill=white,
    )
    draw.rectangle(
        (scaled(675, scale), scaled(410, scale), scaled(790, scale), scaled(620, scale)),
        fill=white,
    )
    draw.rounded_rectangle(
        (scaled(744, scale), scaled(445, scale), scaled(775, scale), scaled(492, scale)),
        radius=scaled(8, scale),
        fill=blue,
    )
    draw.rounded_rectangle(
        (scaled(744, scale), scaled(538, scale), scaled(775, scale), scaled(585, scale)),
        radius=scaled(8, scale),
        fill=blue,
    )
    draw.ellipse(
        (scaled(273, scale), scaled(470, scale), scaled(363, scale), scaled(560, scale)),
        fill=blue,
    )

    draw.ellipse(
        (scaled(610, scale), scaled(596, scale), scaled(910, scale), scaled(896, scale)),
        fill=white,
    )
    draw.ellipse(
        (scaled(636, scale), scaled(622, scale), scaled(884, scale), scaled(870, scale)),
        fill=green,
    )
    check = [
        (scaled(696, scale), scaled(745, scale)),
        (scaled(739, scale), scaled(789, scale)),
        (scaled(827, scale), scaled(693, scale)),
    ]
    draw.line(
        check,
        fill=white,
        width=max(1, scaled(42, scale)),
        joint="curve",
    )
    radius = max(1, scaled(21, scale))
    for x, y in (check[0], check[-1]):
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=white)

    return image.resize((size, size), Image.Resampling.LANCZOS)


preview = draw_icon(1024)
preview.save(ROOT / "EdpEDiskAutoRun-icon-1024.png", optimize=True)

ico_base = draw_icon(256)
ico_base.save(
    ROOT / "EdpEDiskAutoRun.ico",
    format="ICO",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
           (48, 48), (64, 64), (96, 96), (128, 128), (256, 256)],
)
