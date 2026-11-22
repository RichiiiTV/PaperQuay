import os
import sys
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas

target = Path(sys.argv[1])
pages = [
    ("Musterrechnung", "Prüfnummer 457821. Die Rückerstattung beträgt 48,90 Euro."),
    ("Sample letter", "Reference 673942. Please confirm the delivery address."),
]
batch = len(sys.argv) > 2 and sys.argv[2] == "batch"
quality = len(sys.argv) > 2 and sys.argv[2] == "quality"
if quality:
    pages += [
        ("Lieferschein", "Referenznummer 907641. Bitte beachten Sie die beigefügten Unterlagen."),
        ("", ""),
        ("", ""),
    ]
if batch:
    pages = [
        ("Rechnung", "Rechnungsnummer: AB-20261", "Seite 1 von 2"),
        ("", "", ""),
        ("Rechnung", "Rechnungsnummer: AB-20261", "Seite 2 von 2"),
        ("", "", ""),
        ("Rechnung", "Rechnungsnummer: AB-20262", "Seite 1 von 1"),
        ("", "", ""),
        ("", "", ""),
        ("", "", ""),
        ("Lieferschein", "Die Ware wurde am Montag geliefert.", ""),
    ]
fonts = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
width, height = 1900, 2500
pdf = canvas.Canvas(str(target), pagesize=(width * 72 / 400, height * 72 / 400))
for index, page in enumerate(pages):
    title, sentence = page[:2]
    footer = page[2] if batch else ""
    image = Image.new("RGB", (width, height))
    draw = ImageDraw.Draw(image)
    for y in range(height):
        shade = 239 - round(29 * y / height) if title else 250
        draw.line((0, y, width, y), fill=(shade, shade - 5, shade - 15))
    title_font = ImageFont.truetype(str(fonts / "arialbd.ttf"), 96)
    body_font = ImageFont.truetype(str(fonts / "arial.ttf"), 48)
    draw.text((210, 235), title, font=title_font, fill=(36, 61, 75))
    if batch and title:
        draw.text((210, 470), sentence, font=body_font, fill=(68, 68, 73))
        for line in range(10):
            draw.text((210, 630 + line * 84), "Dieses Blatt enthält Informationen zur Lieferung.",
                      font=body_font, fill=(82, 82, 87))
        draw.text((210, 2200), footer, font=body_font, fill=(68, 68, 73))
    elif quality and index == 2:
        body_font = ImageFont.truetype(str(fonts / "arial.ttf"), 42)
        draw.text((680, 1530), "Referenznummer 907641", font=body_font, fill=(166, 166, 171))
        draw.text((185, 2140), "Bitte beachten Sie die beigefügten Unterlagen.",
                  font=body_font, fill=(162, 162, 168))
    elif not batch and title:
        for line in range(20):
            draw.text((210, 470 + line * 71), sentence, font=body_font, fill=(68, 68, 73))
    if quality and index == 4:
        draw.line((300, 1000, 800, 1000), fill=(178, 178, 178), width=6)
    tilted = image if batch and not title else image.rotate(
        2.2, resample=Image.Resampling.BICUBIC, fillcolor=(220, 215, 205))
    if quality and index in (0, 1):
        tilted = tilted.transpose(Image.Transpose.ROTATE_90 if index == 0 else Image.Transpose.ROTATE_180)
    points = (tilted.width * 72 / 400, tilted.height * 72 / 400)
    pdf.setPageSize(points)
    stream = BytesIO()
    tilted.save(stream, "PNG")
    stream.seek(0)
    pdf.drawImage(ImageReader(stream), 0, 0, *points)
    pdf.showPage()
pdf.save()
