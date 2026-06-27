from __future__ import annotations

import re
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import (
    ListFlowable,
    ListItem,
    PageBreak,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)


ROOT = Path(__file__).resolve().parents[1]
DOCS = ROOT / "docs"
OUT = ROOT / "output" / "pdf"


FILES = [
    ("01_documentacion_funcional.md", "01_documentacion_funcional.pdf", "Documentacion funcional"),
    ("02_documentacion_tecnica.md", "02_documentacion_tecnica.pdf", "Documentacion tecnica"),
    ("03_documentacion_despliegue.md", "03_documentacion_despliegue.pdf", "Documentacion de despliegue"),
]


def clean(text: str) -> str:
    text = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    text = re.sub(r"`([^`]+)`", r"<font name='Courier'>\1</font>", text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", text)
    return text


def build_styles():
    base = getSampleStyleSheet()
    return {
        "title": ParagraphStyle(
            "DocTitle",
            parent=base["Title"],
            fontName="Helvetica-Bold",
            fontSize=24,
            leading=30,
            textColor=colors.HexColor("#1C1C1E"),
            spaceAfter=16,
        ),
        "subtitle": ParagraphStyle(
            "Subtitle",
            parent=base["Normal"],
            fontSize=10,
            leading=14,
            textColor=colors.HexColor("#6B7280"),
            spaceAfter=20,
        ),
        "h1": ParagraphStyle(
            "Heading1Custom",
            parent=base["Heading1"],
            fontName="Helvetica-Bold",
            fontSize=16,
            leading=21,
            textColor=colors.HexColor("#7C3AED"),
            spaceBefore=16,
            spaceAfter=8,
        ),
        "h2": ParagraphStyle(
            "Heading2Custom",
            parent=base["Heading2"],
            fontName="Helvetica-Bold",
            fontSize=13,
            leading=17,
            textColor=colors.HexColor("#2D1B69"),
            spaceBefore=12,
            spaceAfter=6,
        ),
        "h3": ParagraphStyle(
            "Heading3Custom",
            parent=base["Heading3"],
            fontName="Helvetica-Bold",
            fontSize=11,
            leading=15,
            textColor=colors.HexColor("#1C1C1E"),
            spaceBefore=9,
            spaceAfter=4,
        ),
        "body": ParagraphStyle(
            "BodyCustom",
            parent=base["BodyText"],
            fontSize=9,
            leading=13,
            textColor=colors.HexColor("#1C1C1E"),
            spaceAfter=6,
        ),
        "bullet": ParagraphStyle(
            "BulletCustom",
            parent=base["BodyText"],
            fontSize=9,
            leading=12,
            leftIndent=10,
        ),
        "code": ParagraphStyle(
            "CodeCustom",
            parent=base["Code"],
            fontName="Courier",
            fontSize=8,
            leading=10,
            textColor=colors.HexColor("#1C1C1E"),
            backColor=colors.HexColor("#F5F5F7"),
            borderColor=colors.HexColor("#E5E7EB"),
            borderWidth=0.5,
            borderPadding=6,
            spaceBefore=4,
            spaceAfter=8,
        ),
    }


def make_cover(title: str, styles):
    data = [
        [Paragraph("<b>Finanzas Personales</b>", styles["h1"])],
        [Paragraph(title, styles["title"])],
        [Paragraph("Documentacion generada para administracion, usuarios y despliegue.", styles["subtitle"])],
        [Paragraph("Identidad: dashboard financiero moderno, permisos por modulo, correo verificado, PostgreSQL y .NET 8.", styles["body"])],
    ]
    table = Table(data, colWidths=[6.4 * inch])
    table.setStyle(
        TableStyle(
            [
                ("BACKGROUND", (0, 0), (-1, -1), colors.HexColor("#F5F5F7")),
                ("BOX", (0, 0), (-1, -1), 1, colors.HexColor("#D4AF37")),
                ("LEFTPADDING", (0, 0), (-1, -1), 24),
                ("RIGHTPADDING", (0, 0), (-1, -1), 24),
                ("TOPPADDING", (0, 0), (-1, -1), 24),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 24),
            ]
        )
    )
    return [Spacer(1, 1.2 * inch), table, PageBreak()]


def markdown_to_flowables(markdown: str, styles):
    story = []
    lines = markdown.splitlines()
    i = 0
    in_code = False
    code_lines: list[str] = []
    bullets: list[ListItem] = []

    def flush_bullets():
        nonlocal bullets
        if bullets:
            story.append(ListFlowable(bullets, bulletType="bullet", leftIndent=18))
            story.append(Spacer(1, 4))
            bullets = []

    while i < len(lines):
        line = lines[i].rstrip()

        if line.startswith("```"):
            if not in_code:
                flush_bullets()
                in_code = True
                code_lines = []
            else:
                story.append(Paragraph(clean("\n".join(code_lines)).replace("\n", "<br/>"), styles["code"]))
                in_code = False
            i += 1
            continue

        if in_code:
            code_lines.append(line)
            i += 1
            continue

        if not line.strip():
            flush_bullets()
            i += 1
            continue

        if line.startswith("# "):
            flush_bullets()
            story.append(Paragraph(clean(line[2:]), styles["title"]))
        elif line.startswith("## "):
            flush_bullets()
            story.append(Paragraph(clean(line[3:]), styles["h1"]))
        elif line.startswith("### "):
            flush_bullets()
            story.append(Paragraph(clean(line[4:]), styles["h2"]))
        elif line.startswith("#### "):
            flush_bullets()
            story.append(Paragraph(clean(line[5:]), styles["h3"]))
        elif line.startswith("- "):
            bullets.append(ListItem(Paragraph(clean(line[2:]), styles["bullet"])))
        elif re.match(r"^\d+\. ", line):
            flush_bullets()
            story.append(Paragraph(clean(line), styles["body"]))
        else:
            flush_bullets()
            story.append(Paragraph(clean(line), styles["body"]))
        i += 1

    flush_bullets()
    return story


def footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(colors.HexColor("#6B7280"))
    canvas.drawString(0.75 * inch, 0.45 * inch, "Finanzas Personales")
    canvas.drawRightString(7.75 * inch, 0.45 * inch, f"Pagina {doc.page}")
    canvas.setStrokeColor(colors.HexColor("#E5E7EB"))
    canvas.line(0.75 * inch, 0.62 * inch, 7.75 * inch, 0.62 * inch)
    canvas.restoreState()


def generate():
    OUT.mkdir(parents=True, exist_ok=True)
    styles = build_styles()
    for md_name, pdf_name, title in FILES:
        md_path = DOCS / md_name
        pdf_path = OUT / pdf_name
        story = make_cover(title, styles)
        story.extend(markdown_to_flowables(md_path.read_text(encoding="utf-8"), styles))
        doc = SimpleDocTemplate(
            str(pdf_path),
            pagesize=letter,
            rightMargin=0.75 * inch,
            leftMargin=0.75 * inch,
            topMargin=0.75 * inch,
            bottomMargin=0.8 * inch,
            title=title,
            author="Finanzas Personales",
        )
        doc.build(story, onFirstPage=footer, onLaterPages=footer)
        print(pdf_path)


if __name__ == "__main__":
    generate()
