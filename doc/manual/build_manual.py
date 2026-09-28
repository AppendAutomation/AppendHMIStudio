#!/usr/bin/env python3
"""Builds the Append HMI Studio User Manual (.docx) from the screenshots in
images/, using the LiquidWeighHMI example (examples/LiquidWeighHMI.ahmi).

    python3 doc/manual/build_manual.py

Writes doc/Append-HMI-Studio-User-Manual.docx; finish.py then fills in the
table of contents and exports the PDF (needs LibreOffice). Tables follow the
house rules: widths computed from their text, fixed layout, header rows
repeated on every page, rows kept whole, captions kept with their tables and
1/8" cell margins.
"""

import os
import re
import subprocess
import sys

from docx import Document
from docx.enum.section import WD_ORIENT
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Inches, Pt, RGBColor
from PIL import Image, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
IMAGES = os.path.join(HERE, 'images')
OUT = os.path.join(HERE, '..', 'Append-HMI-Studio-User-Manual.docx')

FONT = 'Calibri'
# Carlito has Calibri's metrics, so text measured with it fits in Word
FONT_FILES = {
    False: '/usr/share/fonts/truetype/crosextra/Carlito-Regular.ttf',
    True: '/usr/share/fonts/truetype/crosextra/Carlito-Bold.ttf',
}
BODY_PT = 11
TABLE_PT = 10
PAGE_WIDTH_IN = 8.5
MARGIN_IN = 1.0
TEXT_WIDTH_IN = PAGE_WIDTH_IN - 2 * MARGIN_IN
CELL_MARGIN_IN = 0.125
ACCENT = RGBColor(0x1F, 0x5F, 0x99)

_fonts = {}


def text_width_pt(text, bold=False, size=TABLE_PT):
    key = (bold, size)

    if key not in _fonts:
        _fonts[key] = ImageFont.truetype(FONT_FILES[bold], size * 10)

    return _fonts[key].getlength(text) / 10.0


# ------------------------------------------------------------------ tables

def _set_cell_width(cell, width_in):
    cell.width = Inches(width_in)
    tc_pr = cell._tc.get_or_add_tcPr()
    tc_w = tc_pr.find(qn('w:tcW'))

    if tc_w is None:
        tc_w = OxmlElement('w:tcW')
        tc_pr.append(tc_w)

    tc_w.set(qn('w:w'), str(int(width_in * 1440)))
    tc_w.set(qn('w:type'), 'dxa')


def _repeat_header(row):
    tr_pr = row._tr.get_or_add_trPr()
    el = OxmlElement('w:tblHeader')
    el.set(qn('w:val'), 'true')
    tr_pr.append(el)


def _keep_row_together(row):
    row._tr.get_or_add_trPr().append(OxmlElement('w:cantSplit'))


def _set_cell_margins(table, left=CELL_MARGIN_IN, right=CELL_MARGIN_IN, top=0.04, bottom=0.04):
    tbl_pr = table._tbl.tblPr
    mar = OxmlElement('w:tblCellMar')

    for side, inches in (('left', left), ('right', right), ('top', top), ('bottom', bottom)):
        el = OxmlElement('w:' + side)
        el.set(qn('w:w'), str(int(inches * 1440)))
        el.set(qn('w:type'), 'dxa')
        mar.append(el)

    tbl_pr.append(mar)


def _fixed_layout(table):
    table.autofit = False
    table.allow_autofit = False
    tbl_pr = table._tbl.tblPr
    layout = tbl_pr.find(qn('w:tblLayout'))

    if layout is None:
        layout = OxmlElement('w:tblLayout')
        tbl_pr.append(layout)

    layout.set(qn('w:type'), 'fixed')


def _shade(cell, fill):
    tc_pr = cell._tc.get_or_add_tcPr()
    shd = OxmlElement('w:shd')
    shd.set(qn('w:val'), 'clear')
    shd.set(qn('w:color'), 'auto')
    shd.set(qn('w:fill'), fill)
    tc_pr.append(shd)


def column_widths(rows, available_in=TEXT_WIDTH_IN, first_col_bold=False):
    """Widths (inches) from the text. A column's floor is its longest
    unbreakable word (so a one-word header never wraps), its ideal the whole
    text on one line, both with the cell margins and borders. Columns whose
    ideal fits a fair share of the page get it (short label columns never
    wrap); the rest share what is left in proportion to their ideals, clamped
    to their floors. Space left over goes to the columns still wrapping."""
    ncols = len(rows[0])
    pad = 2 * CELL_MARGIN_IN * 72 + 2  # cell margins and borders, points
    floors = [0.0] * ncols
    ideals = [0.0] * ncols

    for r, row in enumerate(rows):
        for c, text in enumerate(row):
            bold = r == 0 or (first_col_bold and c == 0)
            words = re.split(r'\s+', text.strip()) or ['']
            longest = max(text_width_pt(w, bold) for w in words)
            full = max(text_width_pt(line, bold) for line in text.split('\n'))
            floors[c] = max(floors[c], longest + pad)
            ideals[c] = max(ideals[c], full + pad)

    avail = available_in * 72

    if sum(floors) > avail:
        raise SystemExit('Table does not fit in portrait even at its minimum widths: ' + repr(rows[0]) +
            ' (ask before switching the section to landscape)')

    widths = [0.0] * ncols
    remaining = avail
    open_cols = list(range(ncols))

    # Water-filling: every column whose whole text fits a fair share gets it
    while open_cols:
        share = remaining / len(open_cols)
        fits = [c for c in open_cols if ideals[c] <= share]

        if not fits:
            break

        for c in fits:
            widths[c] = ideals[c]
            remaining -= ideals[c]
            open_cols.remove(c)

    if open_cols:
        total = sum(ideals[c] for c in open_cols)

        for c in open_cols:
            widths[c] = remaining * ideals[c] / total

        # Clamp to floors, taking the difference from open columns above theirs
        for _ in range(10):
            short = [c for c in open_cols if widths[c] < floors[c]]

            if not short:
                break

            need = sum(floors[c] - widths[c] for c in short)

            for c in short:
                widths[c] = floors[c]

            donors = [c for c in open_cols if widths[c] > floors[c]]
            spare = sum(widths[c] - floors[c] for c in donors)

            for c in donors:
                widths[c] -= need * (widths[c] - floors[c]) / spare
    else:
        # Nothing wraps: spread what is left in proportion
        total = sum(widths)
        widths = [w * avail / total for w in widths]

    return [w / 72 for w in widths]


def add_table(doc, caption, rows, first_col_bold=False):
    cap = doc.add_paragraph(style='Caption')
    cap.add_run(caption)
    cap.paragraph_format.keep_with_next = True

    widths = column_widths(rows, first_col_bold=first_col_bold)
    table = doc.add_table(rows=len(rows), cols=len(rows[0]))
    table.style = 'Table Grid'
    table.alignment = WD_TABLE_ALIGNMENT.LEFT
    _fixed_layout(table)
    _set_cell_margins(table)

    for c, col in enumerate(table.columns):
        col.width = Inches(widths[c])

    for r, row in enumerate(rows):
        tr = table.rows[r]
        _keep_row_together(tr)

        if r == 0:
            _repeat_header(tr)

        for c, text in enumerate(row):
            cell = tr.cells[c]
            _set_cell_width(cell, widths[c])
            cell.text = ''
            p = cell.paragraphs[0]
            p.paragraph_format.space_after = Pt(0)
            lines = text.split('\n')

            for i, line in enumerate(lines):
                run = p.add_run(line)
                run.font.size = Pt(TABLE_PT)
                run.bold = r == 0 or (first_col_bold and c == 0)

                if i < len(lines) - 1:
                    run.add_break()

            if r == 0:
                _shade(cell, 'D9E2EC')

    doc.add_paragraph()

    return table


# ------------------------------------------------------------------ text

def para(doc, text, style=None):
    p = doc.add_paragraph(style=style)
    _runs(p, text)

    return p


def _runs(p, text):
    """**bold** and `code` inline."""
    for part in re.split(r'(\*\*[^*]+\*\*|`[^`]+`)', text):
        if part.startswith('**') and part.endswith('**'):
            p.add_run(part[2:-2]).bold = True
        elif part.startswith('`') and part.endswith('`'):
            run = p.add_run(part[1:-1])
            run.font.name = 'Consolas'
            run.font.size = Pt(BODY_PT - 1)
        elif part:
            p.add_run(part)


def bullets(doc, items, style='List Bullet'):
    for item in items:
        _runs(doc.add_paragraph(style=style), item)


def steps(doc, items):
    bullets(doc, items, 'List Number')


def note(doc, text):
    p = doc.add_paragraph()
    p.paragraph_format.left_indent = Inches(0.25)
    run = p.add_run('Note: ')
    run.bold = True
    run.font.color.rgb = ACCENT
    _runs(p, text)


def code(doc, text):
    for line in text.strip('\n').split('\n'):
        p = doc.add_paragraph()
        p.paragraph_format.left_indent = Inches(0.3)
        p.paragraph_format.space_after = Pt(0)
        run = p.add_run(line)
        run.font.name = 'Consolas'
        run.font.size = Pt(9.5)

    doc.add_paragraph()


_figures = [0]


def figure(doc, name, caption, max_width_in=TEXT_WIDTH_IN, px_per_in=115):
    path = os.path.join(IMAGES, name + '.png')

    with Image.open(path) as im:
        width_px = im.size[0]

    width = min(max_width_in, width_px / px_per_in)
    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.keep_with_next = True
    p.add_run().add_picture(path, width=Inches(width))
    _figures[0] += 1
    cap = doc.add_paragraph(style='Caption')
    cap.alignment = WD_ALIGN_PARAGRAPH.CENTER
    cap.add_run('Figure %d. %s' % (_figures[0], caption))


_tables = [0]


def table(doc, caption, rows, first_col_bold=True):
    _tables[0] += 1

    return add_table(doc, 'Table %d. %s' % (_tables[0], caption), rows, first_col_bold)


def field(paragraph, instr):
    run = paragraph.add_run()
    begin = OxmlElement('w:fldChar')
    begin.set(qn('w:fldCharType'), 'begin')
    run._r.append(begin)
    run2 = paragraph.add_run()
    text = OxmlElement('w:instrText')
    text.set(qn('xml:space'), 'preserve')
    text.text = instr
    run2._r.append(text)
    run3 = paragraph.add_run()
    sep = OxmlElement('w:fldChar')
    sep.set(qn('w:fldCharType'), 'separate')
    run3._r.append(sep)
    run4 = paragraph.add_run('1' if 'PAGE' in instr else 'Right-click to update the table of contents.')
    run5 = paragraph.add_run()
    end = OxmlElement('w:fldChar')
    end.set(qn('w:fldCharType'), 'end')
    run5._r.append(end)


# ------------------------------------------------------------------ document

def styles(doc):
    normal = doc.styles['Normal']
    normal.font.name = FONT
    normal.font.size = Pt(BODY_PT)
    normal.element.rPr.rFonts.set(qn('w:eastAsia'), FONT)
    normal.paragraph_format.space_after = Pt(6)

    for name, size in (('Heading 1', 18), ('Heading 2', 14), ('Heading 3', 12)):
        s = doc.styles[name]
        s.font.name = FONT
        s.font.size = Pt(size)
        s.font.color.rgb = ACCENT
        s.element.rPr.rFonts.set(qn('w:asciiTheme'), 'minorHAnsi') if False else None
        s.paragraph_format.keep_with_next = True

    cap = doc.styles['Caption']
    cap.font.name = FONT
    cap.font.size = Pt(9.5)
    cap.font.italic = True
    cap.font.color.rgb = RGBColor(0x40, 0x40, 0x40)


def page_setup(doc):
    s = doc.sections[0]
    s.orientation = WD_ORIENT.PORTRAIT
    s.page_width = Inches(PAGE_WIDTH_IN)
    s.page_height = Inches(11)

    for side in ('left_margin', 'right_margin', 'top_margin', 'bottom_margin'):
        setattr(s, side, Inches(MARGIN_IN))

    s.different_first_page_header_footer = True
    fp = s.footer.paragraphs[0]
    fp.alignment = WD_ALIGN_PARAGRAPH.CENTER
    r = fp.add_run('Append HMI Studio User Manual  ·  page ')
    r.font.size = Pt(9)
    field(fp, 'PAGE')

    for run in fp.runs:
        run.font.size = Pt(9)


def cover(doc):
    for _ in range(5):
        doc.add_paragraph()

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.add_run().add_picture(os.path.join(HERE, '..', '..', 'build', '256x256.png'), width=Inches(1.4))

    t = doc.add_paragraph()
    t.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = t.add_run('Append HMI Studio')
    run.bold = True
    run.font.size = Pt(32)
    run.font.color.rgb = ACCENT

    s = doc.add_paragraph()
    s.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = s.add_run('User Manual')
    run.font.size = Pt(22)

    s2 = doc.add_paragraph()
    s2.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = s2.add_run('Designing, testing and deploying HMI applications,\nillustrated with the LiquidWeighHMI example')
    run.font.size = Pt(13)
    run.font.color.rgb = RGBColor(0x55, 0x55, 0x55)

    for _ in range(8):
        doc.add_paragraph()

    v = doc.add_paragraph()
    v.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = v.add_run('Version 1.0  ·  September 2026\nAppend Automation')
    run.font.size = Pt(11)
    doc.add_paragraph().add_run().add_break(WD_BREAK.PAGE)


def contents(doc):
    h = doc.add_paragraph('Contents', style='TOC Heading') if 'TOC Heading' in [s.name for s in doc.styles] else \
        doc.add_heading('Contents', level=1)
    p = doc.add_paragraph()
    field(p, 'TOC \\o "1-2" \\h \\z \\u')
    doc.add_paragraph().add_run().add_break(WD_BREAK.PAGE)


def build():
    doc = Document()
    styles(doc)
    page_setup(doc)
    cover(doc)
    contents(doc)

    H1 = lambda t: doc.add_heading(t, level=1)
    H2 = lambda t: doc.add_heading(t, level=2)
    H3 = lambda t: doc.add_heading(t, level=3)
    P = lambda t: para(doc, t)

    # ------------------------------------------------------------ 1
    H1('1  Introduction')
    P('Append HMI Studio designs operator screens (HMIs) for PLCs, runs them against live equipment and publishes '
      'them for the plant floor. This manual shows how to build an HMI application with it, step by step, using a '
      'complete, working example: **LiquidWeighHMI**, the operator interface of a liquid weigh-up system that '
      'meters three plasticizers into a weigh hopper and discharges each batch to a mixer.')
    P('The example is included with Append HMI Studio as `examples/LiquidWeighHMI.ahmi`. Open it with **File > '
      'Open** and follow along; every screenshot in this manual was taken from it, the run-mode ones with the '
      'application connected to its ControlLogix controller.')

    table(doc, 'What the LiquidWeighHMI example contains', [
        ['Part', 'In the example'],
        ['Target screen', '1920 × 1080, full screen'],
        ['PLC', 'One ControlLogix controller (EtherNet/IP), 419 I/O tags'],
        ['Screens', 'Process, Batch, Supply, Devices, Setpoints, Alarm History and IO Sim'],
        ['Popup windows', 'A faceplate for each of 11 valves and 3 pumps, and 4 confirmation dialogs'],
        ['Alarms', '34 alarmed tags, an Active Alarms list on every screen and an Alarm History screen'],
        ['Security', 'Three users (Operator, Maintenance, Engineer) with access levels'],
        ['Scripts', 'Window scripts that turn the sequence step number into text'],
    ])

    H2('1.1  How an HMI application is organized')
    P('An Append HMI Studio project (`.ahmi` file) holds everything the operator station needs:')
    bullets(doc, [
        '**Devices**: the PLCs the application talks to (Allen-Bradley Logix, SLC 500 and MicroLogix, Modbus TCP, '
        'or the built-in simulator).',
        '**Tags**: named values, either read from and written to a PLC (I/O tags) or kept by the HMI itself '
        '(memory tags). Tags carry engineering units, ranges and alarm limits.',
        '**Windows**: each page of the drawing is a window of the application, either a full screen or a popup '
        'such as a faceplate.',
        '**Objects and animation links**: the shapes on the screens, and the links that tie them to tags: colors '
        'that follow a state, levels that fill, values that display, buttons that write.',
        '**Application settings**: the target screen resolution, the windows that open at start-up, how the '
        'published application runs, and its users.',
    ])
    P('The usual order of work, which this manual follows, is: application settings, devices, tags, screens and '
      'windows, animation, alarms and security, then testing with Run and deploying.')

    # ------------------------------------------------------------ 2
    H1('2  The Studio window')
    figure(doc, 'studio-window', 'Append HMI Studio with the LiquidWeighHMI Process screen open')
    table(doc, 'Parts of the Studio window', [
        ['Area', 'What it is for'],
        ['Menu bar', 'File, Edit, View, Arrange and Extras from the drawing editor, plus the HMI menu for everything '
         'specific to HMI applications'],
        ['Toolbar', 'Zoom, undo, delete, arrange, and quick access to fill, line and shadow'],
        ['Shapes', 'Shape libraries (General, Misc, Advanced and more under More Shapes, including the P&ID '
         'libraries) and the HMI palette with the Alarm List and Alarm History objects'],
        ['Canvas', 'The page being edited. The dashed Screen frame shows the target screen (1920 × 1080 here)'],
        ['Format panel', 'Style, Text and Arrange for the selected object, and the Animation tab for its links'],
        ['Page tabs', 'One tab per window of the application; + adds one'],
    ])

    H2('2.1  The HMI menu')
    figure(doc, 'hmi-menu', 'The HMI menu', max_width_in=3.0)
    table(doc, 'HMI menu commands', [
        ['Command', 'Use'],
        ['Tag Dictionary...', 'Create and edit tags (chapter 5)'],
        ['Devices...', 'Configure the PLCs (chapter 4)'],
        ['Users...', 'Users, passwords and access levels (chapter 10)'],
        ['Application Settings...', 'Target screen, start-up windows, runtime and security settings (chapter 3)'],
        ['Window Properties...', 'Type, position, size and scripts of the current window (chapter 6)'],
        ['Validate Expressions', 'Check every link, script and address for errors (chapter 11)'],
        ['Publish...', 'Build a Windows installer of the application (chapter 12)'],
        ['Run / Stop (F5)', 'Run the application in the Studio against live or simulated data (chapter 11)'],
        ['Runtime Log...', 'Messages from the last runs: connections, writes, script errors'],
        ['Clear Retentive Values...', 'Forget the saved values of retentive tags on this computer'],
        ['Clear Runtime User Changes...', 'Forget user changes made while running on this computer'],
    ])

    # ------------------------------------------------------------ 3
    H1('3  Application settings')
    P('Start a new application by setting its target screen: the resolution of the operator station it will run '
      'on. Open **HMI > Application Settings**.')
    figure(doc, 'app-settings', 'Application Settings: target screen and start-up windows', max_width_in=4.6)
    bullets(doc, [
        '**Resolution**: choose a common size or type the width and height. LiquidWeighHMI targets a 1920 × '
        '1080 panel PC. The dashed Screen frame on every page follows this size.',
        '**Startup windows**: the windows that open when the application starts, in page order. LiquidWeighHMI '
        'opens the Process screen. With none ticked, the page being edited opens.',
    ])
    figure(doc, 'app-settings-runtime', 'Application Settings: runtime and security', max_width_in=4.6)
    bullets(doc, [
        '**Window**: how a published application runs on the target PC: **Kiosk** (full screen, locked, taskbar '
        'hidden), **Full screen**, or a **window** at the target resolution. LiquidWeighHMI uses Full screen.',
        '**Exit**: the exit shortcut Ctrl+Alt+Shift+Q; the shortcut followed by a password; or never (the PC must '
        'be shut down to stop it).',
        '**Log out after (min)**: logs the operator out after this many minutes without a touch or key press. '
        '0 never logs out.',
    ])

    # ------------------------------------------------------------ 4
    H1('4  Devices')
    P('Devices are the PLCs the application communicates with. Open **HMI > Devices**, click **Add**, and fill in '
      'the connection. LiquidWeighHMI has one device, **LiquidWeighPLC**, a ControlLogix controller.')
    figure(doc, 'devices', 'The Devices dialog with the LiquidWeighPLC controller', max_width_in=5.5)
    bullets(doc, [
        '**Protocol**: the communication driver (Table 4).',
        '**IP address or host**, **Port** (44818 for EtherNet/IP, 502 for Modbus TCP) and **Timeout**.',
        '**Processor slot**: the controller\'s slot in the chassis (0 for a CompactLogix).',
        '**Scan rate**: how often subscribed tags are read, 250 ms here.',
        '**Test Connection** checks that the PLC answers before any screen is built.',
    ])
    table(doc, 'Protocols and address examples', [
        ['Protocol', 'Controllers', 'Address examples'],
        ['EtherNet/IP (Logix)', 'ControlLogix, CompactLogix, Micro800', 'PT_001.Value\nProgram:Main.Pump.Run\nRecipe[3]\nStatus.5'],
        ['PCCC (SLC)', 'SLC 5/05, MicroLogix', 'N7:0\nB3:1/4\nF8:2\nT4:0.ACC'],
        ['Modbus TCP', 'Any Modbus TCP device', 'HR:0\nHR:10:FLOAT\nCO:5\nIR:3'],
        ['Simulator', 'None (built in)', 'Simulated values for testing'],
    ])
    note(doc, 'Communication runs through a small companion program, the hmi-comms server, which the Studio starts '
         'by itself. Device states and errors appear in HMI > Runtime Log.')

    # ------------------------------------------------------------ 5
    H1('5  Tags')
    P('Tags are the named values the screens show and change. Open **HMI > Tag Dictionary**. The list on the left '
      'shows every tag with its type; type in **Filter** to find one. Select a tag to edit it, or choose a type '
      'under **New tag...** to create one.')
    figure(doc, 'tag-dictionary', 'The Tag Dictionary with PT001, the weigh hopper pressure', max_width_in=5.8)
    table(doc, 'Tag types', [
        ['Type', 'Holds', 'LiquidWeighHMI example'],
        ['IODiscrete', 'On/off from a PLC', 'SV001_Opened (valve open limit)'],
        ['IOInteger', 'Whole number from a PLC', 'SV001_Status (0 closed, 1 open, 2 travel, 3 fault)'],
        ['IOReal', 'Real number from a PLC', 'PT001 (hopper pressure, psig)'],
        ['IOMessage', 'Text from a PLC', 'Recipe or product names'],
        ['MemoryDiscrete, MemoryInteger, MemoryReal, MemoryMessage', 'A value kept by the HMI', 'StepText (sequence step description)'],
    ])
    H2('5.1  Tag fields')
    table(doc, 'Tag fields', [
        ['Field', 'Meaning'],
        ['Name', 'Letters, digits and underscores; not case-sensitive. LiquidWeighHMI names tags after the device '
         'and signal, such as SV001_Status and PT001'],
        ['Comment', 'A description. For an alarmed tag it is the alarm text operators see'],
        ['Engineering units, Minimum EU, Maximum EU', 'Units and range, used by fills, bars and value displays'],
        ['Initial value', 'Starting value of a memory tag'],
        ['Device, Address', 'For an I/O tag: the PLC and the address in its syntax. The address is checked as you '
         'type (green when valid)'],
        ['Scale raw values', 'Maps a raw integer range (for example 0 to 4095) to the engineering range'],
        ['Retentive', 'For a memory tag: keeps its last value between runs'],
        ['Simulation', 'A simulated value (sine, ramp, random, toggle) for testing without the PLC'],
    ])
    H2('5.2  Alarms on a tag')
    P('Scroll down to **Alarms** to give a tag alarm limits. PT001 alarms at High 70 psig and HiHi 85 psig, with a '
      'deadband of 2 so the alarm clears only below 68 psig. A discrete tag instead has **Alarm when** On or Off: '
      'SV001_Fault alarms when it is on.')
    figure(doc, 'tag-alarms', 'Alarm limits and deadband for PT001', max_width_in=5.8)
    P('Tags can also be exported to and imported from CSV (**Export CSV**, **Import CSV...**), which is the fastest '
      'way to create hundreds of tags from a PLC tag list: LiquidWeighHMI\'s 420 tags were entered that way.')

    # ------------------------------------------------------------ 6
    H1('6  Screens and windows')
    P('Each page of the drawing is a window of the application, named by its page tab. LiquidWeighHMI has seven '
      'full screens and eighteen popups.')
    figure(doc, 'pages-tabs', 'Page tabs: every page is a window')
    table(doc, 'Window types', [
        ['Type', 'Behavior', 'Used in LiquidWeighHMI for'],
        ['Replace', 'Closes any window it overlaps when it opens', 'The seven main screens'],
        ['Overlay', 'Opens on top and leaves other windows open', 'Information panels'],
        ['Popup (modal)', 'Stays on top; nothing beneath it can be touched until it closes', 'Faceplates and confirmations'],
    ])
    H2('6.1  Window properties')
    P('Select a page and open **HMI > Window Properties**. For the SV-001 faceplate the window is a popup with a '
      'title bar, 460 × 560 pixels at 730, 150 on the screen. The preview shows where it appears.')
    figure(doc, 'window-popup', 'Window Properties of the SV-001 Faceplate popup', max_width_in=4.4)
    P('A window shows the part of its page under its rectangle, so a popup\'s objects are drawn at the popup\'s '
      'position on its page. Figure 10 shows the SV-001 faceplate page in the editor.')
    figure(doc, 'faceplate-edit', 'The SV-001 faceplate page in the editor')
    H2('6.2  Navigation')
    P('The navigation bar at the bottom of every LiquidWeighHMI screen is a row of buttons, each with a **Show '
      'Window** link to one screen. Because the main screens are Replace windows, opening one closes the current '
      'screen. Faceplates open the same way from the valve and pump symbols, and close with a **Hide Window** link '
      'on their DONE button.')

    # ------------------------------------------------------------ 7
    H1('7  Drawing objects')
    P('Screens are drawn with the editor\'s shapes: drag a shape from the Shapes panel onto the canvas, then set its '
      'fill, line and text in the Format panel. LiquidWeighHMI uses:')
    bullets(doc, [
        'Rectangles and rounded rectangles for panels, buttons and value boxes.',
        'Process symbols from **More Shapes > P&ID** (valves, pumps, vessels) and plain shapes for tanks and totes.',
        'Lines and connectors for piping.',
        'Text for labels and titles.',
    ])
    P('The **HMI** palette adds two objects: the **Alarm List**, the live list of active alarms at the bottom of '
      'every LiquidWeighHMI screen, and the **Alarm History**, used on the Alarm History screen.')
    figure(doc, 'hmi-palette', 'The HMI palette: Alarm List and Alarm History', max_width_in=2.2)
    note(doc, 'Keep objects inside the Screen frame, and group repeated symbols (a valve with its tag label) so '
         'they can be copied with their links.')

    # ------------------------------------------------------------ 8
    H1('8  Animation links')
    P('Animation links make objects live. Select an object and open the **Animation** tab of the Format panel. The '
      'top of the tab lists every link type; click one to add it. Links already on the object are marked, and '
      'their settings appear below.')
    figure(doc, 'launcher-panel', 'The Animation tab for the START button, with its links marked', max_width_in=2.8)
    table(doc, 'Animation link families', [
        ['Family', 'Links', 'What they do'],
        ['Touch links', 'User Input, Pushbutton, Action Script, Show Window, Hide Window, Slider Horizontal and Vertical',
         'Operator actions: enter a value, set or toggle a tag, run a script, open or close a window, drag a slider'],
        ['Line, Fill and Text Color', 'Discrete, Analog, Discrete Alarm, Analog Alarm',
         'Color from an on/off value, from value bands, or from a tag\'s alarm state'],
        ['Value / Movement', 'Location, Width, Height, Percent Fill, Orientation',
         'Move, size, fill or rotate an object in proportion to a value'],
        ['Miscellaneous', 'Visibility, Blink, Enable', 'Show or hide, flash, and allow or block touch'],
        ['Value Display', 'Value Display', 'Show a value or text in place of the object\'s label'],
    ])
    P('Numbers in links (ranges, limits) are expressions too, so they can come from tags. The sections below walk '
      'through the links LiquidWeighHMI uses.')

    H2('8.1  A valve: status color, fault blink and faceplate')
    P('Each valve symbol on the Process screen carries three links. SV-102 is selected in Figure 13.')
    figure(doc, 'animation-valve', 'The SV-102 valve and its links')
    bullets(doc, [
        '**Fill Color / Analog** on `SV102_Status`: below 0.5 (closed) white, below 1.5 (open) green, below 2.5 '
        '(travel) amber, otherwise (fault) red. The first band whose limit the value is below wins.',
        '**Blink** on `SV102_Fault AND NOT SV102_FaultAck`: flashes the fill while a fault is unacknowledged.',
        '**Show Window** SV-102 Faceplate: touching the valve opens its faceplate.',
    ])

    H2('8.2  The weigh hopper: percent fill')
    P('The hopper vessel fills with its net weight. **Percent Fill / Vertical** maps `WT001` from 0 to 150 lb to 0 to '
      '100 % fill, from the bottom up.')
    figure(doc, 'animation-hopper', 'Percent Fill on the weigh hopper', max_width_in=2.4)
    note(doc, 'Percent Fill draws only the filled part of the object, outline included. Place an identical shape '
         'with no fill on top as the vessel outline, as LiquidWeighHMI does.')

    H2('8.3  Hopper pressure: value display and alarm colors')
    P('The pressure box shows `PT001` with one decimal and the suffix psig (**Value Display**), and its fill follows '
      'the tag\'s alarm state (**Fill Color / Analog Alarm**): normal white, High amber, HiHi red. The limits come '
      'from the tag in the Tag Dictionary, so they are set in one place.')
    figure(doc, 'animation-alarm', 'Value Display and Fill Color / Analog Alarm on PT001', max_width_in=2.4)

    H2('8.4  Setpoints: user input')
    P('On the Batch screen the operator enters the batch volume by touching its value. **User Input** (analog) on '
      '`Rcp_TotalVol` accepts 0 to 12 gallons with the prompt "Total batch volume (gal, max 12)" and shows an '
      'on-screen keypad. A **Value Display** on the same object shows the current value.')
    figure(doc, 'animation-input', 'User Input and Value Display on the batch volume', max_width_in=2.4)

    H2('8.5  Commands: pushbutton and enable')
    P('The START button on the Batch screen is a **Pushbutton** that sets `PB_Start` when touched, allowed only '
      'while `Seq_Idle AND Seq_StartPerm` (Enable when). Its fill and text colors show when it is available. An '
      '**Enable** link on `_AccessLevel > 0` blocks it until someone is logged in (chapter 10).')
    figure(doc, 'animation-start', 'The START button: Pushbutton, colors and Enable')
    table(doc, 'Pushbutton actions', [
        ['Action', 'Effect'],
        ['Toggle', 'Inverts the tag on each touch'],
        ['Set', 'Writes 1 (START, HOLD, RESUME in LiquidWeighHMI)'],
        ['Reset', 'Writes 0'],
        ['Direct', '1 while pressed, 0 when released (jog buttons)'],
    ])

    # ------------------------------------------------------------ 9
    H1('9  Expressions and scripts')
    P('Links use expressions, and scripts (Action Script links and window scripts) use statements, both in the '
      'InTouch QuickScript style.')
    table(doc, 'Expression syntax', [
        ['Element', 'Examples'],
        ['Tags and dotfields', 'PT001, SV001_Status, PT001.InAlarm, PT001.Acked, PT001.MaxEU'],
        ['Operators', '+ - * / MOD, == <> < <= > >=, AND OR NOT, parentheses'],
        ['Literals', '12.5, "IDLE"'],
        ['Functions', 'Abs, Sqrt, Int, Round, Min(a, b), Max(a, b), StringLen, Text(value, "0.00")'],
        ['Statements (scripts)', 'Pump_Run = 1;  IF cond THEN ... ELSE ... ENDIF;'],
    ])
    table(doc, 'System tags', [
        ['Tag', 'Value'],
        ['_AlarmsActive', 'Number of active alarms (the Alarms: n box in LiquidWeighHMI\'s header)'],
        ['_AlarmsUnacked', 'Number of unacknowledged alarms'],
        ['_AckAll', 'Write 1 to acknowledge every alarm'],
        ['_Username', 'Logged-in user, or None'],
        ['_AccessLevel', 'Logged-in user\'s access level, or 0'],
    ])
    H2('9.1  Window scripts')
    P('A window can run a script when it opens (**On show**), repeatedly while it is open (**While showing**, every '
      'n ms) and when it closes (**On hide**). Every LiquidWeighHMI screen turns the PLC\'s sequence step number '
      'into the text shown in the header, four times a second:')
    code(doc, '''
IF Seq_Step == 0 THEN StepText = "IDLE"; ENDIF;
IF Seq_Step == 5 THEN StepText = "TARE"; ENDIF;
IF Seq_Step == 11 THEN StepText = "PLAST #1 FAST FILL"; ENDIF;
...
''')
    figure(doc, 'window-scripts', 'Window scripts of the Process screen (While showing every 250 ms)', max_width_in=4.4)
    H2('9.2  Action scripts')
    P('An **Action Script** link runs statements when the object is pressed (On down), repeatedly while held '
      '(While down) and when released (On up). The LOGIN button in LiquidWeighHMI\'s header logs in or out:')
    code(doc, '''
IF _Username == "None" THEN ShowLogin(); ELSE Logout(); ENDIF;
''')
    figure(doc, 'animation-login', 'The LOGIN button: Action Script, Value Display and colors', max_width_in=2.4)

    # ------------------------------------------------------------ 10
    H1('10  Alarms and security')
    H2('10.1  Alarms')
    P('Alarms come from the limits set on tags (chapter 5). While the application runs, every alarmed tag is '
      'watched, whether or not a screen shows it:')
    bullets(doc, [
        'Entering alarm, or escalating (High to HiHi), raises an alarm that needs acknowledging.',
        'An alarm stays in the Alarm List until it has returned to normal **and** been acknowledged (ISA-18.2).',
        'The Alarm List object has an **Ack All** button and an **Ack** button on each row. A script can '
        'acknowledge with `_AckAll = 1;` or `Tag.Acked = 1;`.',
        'Every alarm event is written to a daily history file kept for 90 days; the Alarm History object shows '
        'it (Figure 30).',
    ])
    H2('10.2  Users and access levels')
    P('Open **HMI > Users** to define who can log in. Each user has a name, a password (stored only as a salted '
      'hash) and an access level from 0 to 9999.')
    figure(doc, 'users', 'The Users dialog with LiquidWeighHMI\'s three users', max_width_in=4.4)
    table(doc, 'LiquidWeighHMI users', [
        ['User', 'Access level', 'Can'],
        ['Operator', '1000', 'Run batches'],
        ['Maintenance', '2000', 'Also operate devices in manual'],
        ['Engineer', '9999', 'Everything, including setpoints and simulation'],
    ])
    P('Screens use `_AccessLevel` to protect controls: an **Enable** link such as `_AccessLevel >= 2000` blocks '
      'touch on an object, and a **Visibility** link can hide it. The script function `ShowLogin()` opens the '
      'built-in login window (Figure 21); `Login(name, password)`, `Logout()`, `ChangePassword(old, new)` and '
      '`ShowUserManager()` support custom login screens.')
    figure(doc, 'run-login', 'The login window opened by ShowLogin()', max_width_in=4.0)

    # ------------------------------------------------------------ 11
    H1('11  Testing')
    H2('11.1  Validate')
    P('**HMI > Validate Expressions** checks every link, script and PLC address and lists any problem with the '
      'page and object; click a problem to go to it. Validate before every Run and Publish.')
    figure(doc, 'validate', 'Validation of LiquidWeighHMI: no problems found', max_width_in=4.0)
    H2('11.2  Run')
    P('**HMI > Run** (F5) runs the application inside the Studio: the drawing is locked, the start-up windows '
      'open, and the application connects to its PLCs. A green RUNNING banner shows that the screen is live and '
      'that touching it operates real equipment. **HMI > Stop** returns to editing.')
    figure(doc, 'run-window', 'LiquidWeighHMI running in the Studio')
    P('The screens below were captured while LiquidWeighHMI ran against its controller, with a batch held on a '
      'plasticizer supply alarm.')
    figure(doc, 'run-process', 'Process screen: tank and hopper levels, valve states, sequence and active alarms')
    figure(doc, 'run-faceplate', 'SV-001 faceplate: mode, commands, interlocks and fault reset')
    figure(doc, 'run-batch', 'Batch screen: recipe, sequence control, start permissives and batch progress')
    figure(doc, 'run-setpoints', 'Setpoints screen: materials, weighing, pressure and timing')
    figure(doc, 'run-devices', 'Devices screen: every valve and pump with mode, state and faceplate')
    figure(doc, 'run-supply', 'Supply screen: plasticizer tank, refill station and totes')
    figure(doc, 'run-alarm-history', 'Alarm History screen')
    note(doc, 'HMI > Runtime Log lists connections, device errors, writes and script errors from the run.')

    # ------------------------------------------------------------ 12
    H1('12  Deploying')
    P('An application can reach the plant floor in three ways.')
    table(doc, 'Deployment options', [
        ['Option', 'What the target gets', 'Best for'],
        ['HMI > Publish', 'A Windows installer of this one application, with its own name and icon, starting '
         'straight into it', 'A dedicated, locked-down operator station'],
        ['Append HMI Desktop', 'One runner installed once; runs any .ahmi file, from shortcuts or at log-in',
         'Stations whose projects change often; Linux'],
        ['Append HMI Web', 'A web server; operators open the application in any browser', 'Viewing and operating from '
         'tablets, laptops and control-room PCs'],
    ])
    H2('12.1  Publish')
    P('**HMI > Publish** builds a Windows installer from the open project. Give the product name, version and '
      'publisher, choose an icon, whether it installs for all users, and whether it adds a desktop shortcut or '
      'starts with Windows. Run the Setup.exe on the target PC; the application then starts full screen as set '
      'in Application Settings.')
    figure(doc, 'publish', 'The Publish dialog', max_width_in=4.0)
    H2('12.2  Append HMI Desktop')
    P('Append HMI Desktop runs .ahmi files without the editor. Choose an application, then **Run now**, create a '
      'desktop shortcut or menu entry, or tick **Run this application automatically when I log in**. From the '
      'command line: `append-hmi-desktop LiquidWeighHMI.ahmi`.')
    figure(doc, 'desktop-launcher', 'The Append HMI Desktop launcher', max_width_in=4.2)
    H2('12.3  Append HMI Web')
    P('Append HMI Web serves an application to web browsers on the plant network. Choose the application and a port '
      '(8480 by default), then **Start web server**; the launcher shows the link to share, with a **Copy** button. '
      'In the browser, the view button in the corner fits the screen to the window, maximizes it, or shows it at '
      'its original size with scroll bars.')
    figure(doc, 'web-launcher', 'The Append HMI Web launcher with a running server', max_width_in=4.2)
    figure(doc, 'web-browser', 'An application in a web browser, with the view menu open')

    # ------------------------------------------------------------ 13
    H1('13  Tips and troubleshooting')
    table(doc, 'Common problems', [
        ['Symptom', 'Cause and remedy'],
        ['Values show ####', 'The tag has bad quality: the PLC is not connected or the address is wrong. Check '
         'HMI > Runtime Log and Test Connection in HMI > Devices'],
        ['PLC COMM FAIL in the header', 'The device is unreachable. Check the IP address, slot and network'],
        ['A button does nothing', 'An Enable or Pushbutton "Enable when" condition is false, or nobody with the '
         'access level is logged in'],
        ['Validate reports an expression error', 'Expressions take no ";" (only script statements do); tag names '
         'must exist in the Tag Dictionary'],
        ['A popup opens in the wrong place', 'Its objects must be drawn at the window\'s position on its page '
         '(Window Properties > Left, Top)'],
        ['Retentive values are not what you expect', 'Use HMI > Clear Retentive Values to start from the initial '
         'values again'],
    ])
    P('Build a symbol once, with its links, group it, and copy it: links travel with the copy. The Animation tab '
      'can also copy links from one object and paste them onto others.')

    # ------------------------------------------------------------ A
    H1('Appendix A  Command-line automation')
    P('Append HMI Studio can build, check, render and publish applications without its window, for scripting and '
      'for AI agents:')
    table(doc, 'Command-line modes', [
        ['Command', 'Does'],
        ['--hmi-build spec.json -o app.ahmi', 'Builds a project from a JSON description'],
        ['--hmi-check app.ahmi', 'Validates it (exit code 2 when problems are found)'],
        ['--hmi-render app.ahmi -o folder', 'Renders every page to PNG'],
        ['--hmi-dump app.ahmi -o spec.json', 'Writes a project back out as JSON'],
        ['--hmi-publish app.ahmi --product "Name" --app-version 1.0.0 -o folder', 'Builds the Windows installer'],
    ])
    P('The JSON format is documented in doc/HMI_AUTOMATION.md in the Append HMI Studio repository.')

    doc.save(OUT)
    print('Wrote ' + os.path.relpath(OUT))


if __name__ == '__main__':
    build()
