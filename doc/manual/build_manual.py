#!/usr/bin/env python3
"""Builds the Append HMI Studio User Manual (.docx) from the screenshots in
images/, using the LiquidWeighHMI example (examples/LiquidWeighHMI.ahmi).

    python3 doc/manual/build_manual.py

Writes doc/Append-HMI-Studio-User-Manual.docx; finish.py then fills in the
table of contents and exports the PDF (needs LibreOffice). Tables follow the
house rules through the docx skill (~/.claude/skills/docx): widths computed
from their text, fixed layout, header rows repeated on every page, rows kept
whole, captions kept with their tables and 1/8" cell margins. The skill's
pagination pass then breaks the page before any heading left in the lower
third of a page; finish.py changes only the contents pages, which end with a
page break, so the chapters' layout stays as checked.
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
from PIL import Image

sys.path.insert(0, os.path.expanduser('~/.claude/skills/docx'))
from docx_tables import format_table, usable_width_in  # noqa: E402
from docx_paginate import enforce  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
IMAGES = os.path.join(HERE, 'images')
OUT = os.path.join(HERE, '..', 'Append-HMI-Studio-User-Manual.docx')

FONT = 'Calibri'
BODY_PT = 11
TABLE_PT = 10
PAGE_WIDTH_IN = 8.5
MARGIN_IN = 1.0
TEXT_WIDTH_IN = PAGE_WIDTH_IN - 2 * MARGIN_IN
ACCENT = RGBColor(0x1F, 0x5F, 0x99)

# ------------------------------------------------------------------ tables

def _shade(cell, fill):
    tc_pr = cell._tc.get_or_add_tcPr()
    shd = OxmlElement('w:shd')
    shd.set(qn('w:val'), 'clear')
    shd.set(qn('w:color'), 'auto')
    shd.set(qn('w:fill'), fill)
    tc_pr.append(shd)


def add_table(doc, caption, rows, first_col_bold=False):
    cap = doc.add_paragraph(style='Caption')
    cap.add_run(caption)
    cap.paragraph_format.keep_with_next = True

    table = doc.add_table(rows=len(rows), cols=len(rows[0]))
    table.style = 'Table Grid'

    for r, row in enumerate(rows):
        for c, text in enumerate(row):
            cell = table.rows[r].cells[c]
            cell.text = ''
            p = cell.paragraphs[0]
            lines = text.split('\n')

            for i, line in enumerate(lines):
                before = len(p.runs)
                _runs(p, line)

                if r == 0 or (first_col_bold and c == 0):
                    for run in p.runs[before:]:
                        run.bold = True

                if i < len(lines) - 1:
                    p.runs[-1].add_break()

            if r == 0:
                _shade(cell, 'D9E2EC')

    # The house table rules: computed widths, fixed layout, repeated header,
    # rows kept whole, 1/8" cell margins
    format_table(table, usable_width_in(doc.sections[-1]), size_pt=TABLE_PT)
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
            run.font.size = Pt(BODY_PT - 1.5)
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
_figure_names = {}
_known_figures = {}


def F(name):
    """'Figure n' for the figure called name, wherever it is."""
    return 'Figure %d' % _known_figures.get(name, 0)


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
    _figure_names[name] = _figures[0]
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
    run = v.add_run('Version 1.1  ·  October 2026\nAppend Automation')
    run.font.size = Pt(11)
    doc.add_paragraph().add_run().add_break(WD_BREAK.PAGE)


def contents(doc):
    h = doc.add_paragraph('Contents', style='TOC Heading') if 'TOC Heading' in [s.name for s in doc.styles] else \
        doc.add_heading('Contents', level=1)
    p = doc.add_paragraph()
    field(p, 'TOC \\o "1-2" \\h \\z \\u')
    doc.add_paragraph().add_run().add_break(WD_BREAK.PAGE)


def build(save=True):
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
        ['Screens', 'Process, Batch, Supply, Devices, Setpoints, Recipes, Alarm History and IO Sim'],
        ['Popup windows', 'One faceplate for all 11 valves and 3 pumps, through indirect tags, and 4 confirmation dialogs'],
        ['Alarms', '34 alarmed tags, an Active Alarms list on every screen and an Alarm History screen'],
        ['Security', 'Three users (Operator, Maintenance, Engineer) with access levels'],
        ['Recipes', 'A recipe book of plasticizer blends with five starting recipes, and a Recipes screen to '
         'select, edit, save and download them'],
        ['Scripts', 'Window scripts that turn the sequence step number into text, and action scripts that call '
         'the login and recipe functions'],
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
        ['Recipes...', 'Recipe books: the tags a recipe holds and the starting recipes (chapter 11)'],
        ['Application Settings...', 'Target screen, start-up windows, runtime and security settings (chapter 3)'],
        ['Window Properties...', 'Type, position, size and scripts of the current window (chapter 6)'],
        ['Validate Expressions', 'Check every link, script and address for errors (chapter 12)'],
        ['Publish...', 'Build a Windows installer of the application (chapter 13)'],
        ['Run / Stop (F5)', 'Run the application in the Studio against live or simulated data (chapter 12)'],
        ['Runtime Log...', 'Messages from the last runs: connections, writes, script errors'],
        ['Clear Retentive Values...', 'Forget the saved values of retentive tags on this computer'],
        ['Clear Runtime User Changes...', 'Forget user changes made while running on this computer'],
        ['Clear Runtime Recipes...', 'Forget the recipes saved while running on this computer'],
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
        ['IndirectDiscrete, IndirectAnalog, IndirectMessage', 'No value of its own: stands for the tag a script links it '
         'to with LinkIndirectTag', 'FP_Status: the status of the device the faceplate shows'],
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
    P('Select a page and open **HMI > Window Properties**. The Device Faceplate is a popup with a title bar, '
      '460 × 560 pixels at 730, 150 on the screen. The preview shows where it appears.')
    figure(doc, 'window-popup', 'Window Properties of the Device Faceplate popup', max_width_in=4.4)
    P('A window shows the part of its page under its rectangle, so a popup\'s objects are drawn at the popup\'s '
      'position on its page. ' + F('faceplate-edit') + ' shows the Device Faceplate page in the editor. It serves '
      'every valve and pump (section 9.4), so a few objects for valves and for pumps share a place; visibility links '
      'show the right one while running.')
    figure(doc, 'faceplate-edit', 'The Device Faceplate page in the editor')
    H2('6.2  Navigation')
    P('The navigation bar at the bottom of every LiquidWeighHMI screen is a row of buttons, each with a **Show '
      'Window** link to one screen. Because the main screens are Replace windows, opening one closes the current '
      'screen. The valve and pump symbols open the Device Faceplate the same way, after an Action Script has told it '
      'which device to show (section 9.4); its DONE button closes it with a **Hide Window** link.')

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
    P('Each valve symbol on the Process screen carries four links. SV-102 is selected in ' + F('animation-valve') + '.')
    figure(doc, 'animation-valve', 'The SV-102 valve and its links')
    bullets(doc, [
        '**Fill Color / Analog** on `SV102_Status`: below 0.5 (closed) white, below 1.5 (open) green, below 2.5 '
        '(travel) amber, otherwise (fault) red. The first band whose limit the value is below wins.',
        '**Blink** on `SV102_Fault AND NOT SV102_FaultAck`: flashes the fill while a fault is unacknowledged.',
        '**Action Script** (On down): `FP_Prefix = "SV102"; FP_Device = "SV-102"; ...` tells the faceplate which '
        'device to show (section 9.4).',
        '**Show Window** Device Faceplate: touching the valve then opens the faceplate.',
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
        ['Functions', 'Abs(x), Round(x), Text(value, "0.00") and the others in section 9.3'],
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

    H2('9.3  Function reference')
    P('Functions take their arguments in parentheses, separated by commas. Any argument can be a literal '
      '(`12.5`, `"Standard Blend"`), a tag or a whole expression. Function names are not case-sensitive.')
    bullets(doc, [
        '**Math and text functions** return a value and can be used anywhere: in animation links and in scripts.',
        '**Security, indirect tag and recipe functions** do something (open a window, log in, link or write tags), '
        'so they can be '
        'called only from scripts: Action Scripts and window scripts. Validate reports one used in an animation '
        'link.',
        'Functions that succeed or fail return **1** or **0**, so a script can test them: '
        '`IF Login(LoginName, LoginPassword) THEN ... ENDIF;`.',
    ])
    table(doc, 'Math and text functions (links and scripts)', [
        ['Function', 'Arguments', 'What it does'],
        ['Abs(x)', 'x: a number', 'The absolute value of x. `Abs(-3.5)` is 3.5'],
        ['Sqrt(x)', 'x: a number, 0 or more', 'The square root of x'],
        ['Sqr(x)', 'x: a number', 'x squared (x × x)'],
        ['Int(x)', 'x: a number', 'x with its fraction dropped. `Int(7.9)` is 7 and `Int(-7.9)` is -7'],
        ['Round(x)', 'x: a number', 'x rounded to the nearest whole number; a half rounds up. `Round(2.5)` is 3'],
        ['Min(a, b)', 'a, b: numbers', 'The smaller of a and b. `Min(PT001, 50)` is never more than 50'],
        ['Max(a, b)', 'a, b: numbers', 'The larger of a and b'],
        ['StringLen(text)', 'text: text', 'The number of characters in text. '
         '`StringLen(RecipeName) == 0` is true when no recipe is selected'],
        ['Text(value, format)', 'value: a number\nformat: text', 'The number as text, formatted by a picture such as "0.00". '
         'The digits after the point in format give the decimals, and the digits before it the minimum number of '
         'whole digits, padded with zeros. `Text(3.14159, "0.00")` is "3.14" and `Text(7, "000")` is "007"'],
    ])
    table(doc, 'Security functions (scripts only)', [
        ['Function', 'Arguments', 'What it does'],
        ['ShowLogin()', 'None', 'Opens the login window: user name, masked password and an on-screen keyboard. The '
         'window checks the password and logs the user in. Returns 1'],
        ['Login(name, password)', 'name, password: text', 'Logs in when the name and '
         'password match a user. Returns 1, or 0 when they do not. For custom login screens'],
        ['Logout()', 'None', 'Logs the current user out: `_Username` becomes None and `_AccessLevel` 0. Returns 1'],
        ['ChangePassword(old, new)', 'old, new: text', 'Changes the logged-in user\'s password from old (the '
         'current password) to new (not empty), saved on this computer. Returns 1, or 0 when old is wrong or new is empty'],
        ['ShowUserManager()', 'None', 'Opens the Users window while running, to add, change or remove users on '
         'this computer (section 10.2). Returns 1'],
    ])
    table(doc, 'Indirect tag function (scripts only)', [
        ['Function', 'Arguments', 'What it does'],
        ['LinkIndirectTag(indirect, tag)', 'indirect, tag: tag names as text', 'Points the indirect tag at the tag, '
         'replacing any earlier link; reading or writing the indirect tag then reads or writes that tag, in every '
         'window. The names can be built: `LinkIndirectTag("FP_Status", "SV" + Text(Unit, "000") + "_Status")`. '
         'Returns 1, or 0 with an Indirect Tag Error window when a name is unknown or the types do not match'],
    ])
    P('In the recipe functions, `book` is the name of a recipe book defined in **HMI > Recipes** and `name` the '
      'name of a recipe in it: text of 1 to 64 characters. Names are not case-sensitive. Chapter 11 shows them at '
      'work.')
    table(doc, 'Recipe functions (scripts only)', [
        ['Function', 'Arguments', 'What it does'],
        ['RecipeSave(book, name)', 'book, name', 'Saves the current values of the book\'s Save/Load tags as recipe '
         'name, replacing a recipe of that name. Fails if any of the values has bad quality'],
        ['RecipeLoad(book, name)', 'book, name', 'Writes the recipe\'s values to the book\'s Save/Load tags'],
        ['RecipeDownload(book, name)', 'book, name', 'Copies each Save/Load tag\'s value to its Upload/Download tag, '
         'usually sending the recipe to the PLC. name is not used: give "" or the selected recipe'],
        ['RecipeUpload(book, name)', 'book, name', 'Copies each Upload/Download tag\'s value back to its Save/Load '
         'tag, usually bringing the PLC\'s values into the HMI. name is not used'],
        ['RecipeExport(book, name)', 'book, name', 'Writes the recipe to a CSV file that the operator chooses (in '
         'Append HMI Web, the browser downloads it). Returns 1 when the file window opens'],
        ['RecipeImport(book, name)', 'book, name', 'Reads a recipe CSV file that the operator chooses and saves it as '
         'recipe name. Returns 1 when the file window opens'],
        ['RecipeDelete(book, name, confirm)', 'book, name\nconfirm: optional',
         'Deletes the recipe. With confirm 1, a Yes/No window asks the operator first; No returns 0'],
        ['RecipeRename(book, name, newName)', 'book, name, newName',
         'Renames the recipe to newName, which must not already be in the book'],
        ['ShowRecipeSelect(book, x, y, w, h)', 'book\nx, y, w, h: optional', 'Opens a window listing the book\'s recipes, with up and down arrow buttons, Select and '
         'Cancel, and returns the chosen name, or "" for Cancel. The script waits until the operator chooses, so it '
         'must be a statement on its own or the whole right side of an assignment: `Pick = ShowRecipeSelect("B");`. '
         'x, y, w, h place the window: position and size in screen pixels'],
    ])
    P('The recipe functions other than ShowRecipeSelect return 1 when they succeed and 0 when they fail. A failure '
      'also opens the Recipe Error window (section 11.5).')

    H2('9.4  Indirect tags: one faceplate for every device')
    P('LiquidWeighHMI has 11 valves and 3 pumps, and one **Device Faceplate** for all of them. Its objects use '
      '**indirect tags** (section 5): `FP_Status`, `FP_Manual`, `FP_CmdOn` and so on, which have no value of their '
      'own. When the faceplate opens, a script links each of them to the tag of the device being shown, so '
      '`FP_Status` reads `SV102_Status` for SV-102 and `P100_Status` for P-100. A change to the faceplate is made '
      'once and applies to every device.')
    P('Opening it takes two steps. First, the valve or pump symbol\'s Action Script names the device in memory '
      'tags:')
    code(doc, """
FP_Prefix = "SV102"; FP_Device = "SV-102";
FP_Desc = "Plast #1 bulk tank refill valve";
FP_IsPump = 0; FP_HasLimits = 1; FP_OperatorOnly = 1;
""")
    P('Then its Show Window link opens the faceplate, whose **On show** script builds each tag name from the prefix '
      'and links the indirect tags. A pump links the shared command tags to its Start and Stop, a valve to Open and '
      'Close:')
    code(doc, """
LinkIndirectTag("FP_Status", FP_Prefix + "_Status");
LinkIndirectTag("FP_Manual", FP_Prefix + "_Manual");
...
IF FP_IsPump THEN
    LinkIndirectTag("FP_CmdOn", FP_Prefix + "_CmdStart");
    LinkIndirectTag("FP_CmdOff", FP_Prefix + "_CmdStop");
    LinkIndirectTag("FP_Time", FP_Prefix + "_FailTime");
ELSE
    LinkIndirectTag("FP_CmdOn", FP_Prefix + "_CmdOpen");
    LinkIndirectTag("FP_CmdOff", FP_Prefix + "_CmdClose");
    LinkIndirectTag("FP_Time", FP_Prefix + "_TravelTime");
    IF FP_HasLimits THEN
        LinkIndirectTag("FP_ZSO", FP_Prefix + "_ZSO");
        LinkIndirectTag("FP_ZSC", FP_Prefix + "_ZSC");
    ENDIF;
ENDIF;
""")
    bullets(doc, [
        '**Heading:** Value Displays of `FP_Device` and `FP_Desc`.',
        '**Valve or pump:** visibility links on `FP_IsPump` choose the symbol and status colors. Texts that differ, '
        'such as OPEN or START, are Discrete Value Displays of `FP_IsPump`.',
        '**Variants:** `FP_HasLimits` shows the limit-switch lamps or a note, and `FP_OperatorOnly` replaces the mode '
        'buttons of the refill valve with a note.',
        '**Status:** a While showing script turns `FP_Status` into the words shown (`FP_StateText`).',
        '**Dotfields:** `FP_Status.Name` would show `SV102_Status`; every dotfield describes the linked tag.',
    ])
    figure(doc, 'run-faceplate-pump', 'The same faceplate showing pump P-100')
    note(doc, 'Writing to an indirect tag writes the linked tag: the faceplate\'s MANUAL button sets `FP_CmdManual`, '
         'which is `SV100_CmdManual` while SV-100 is shown. Before the first link an indirect tag has bad quality '
         'and writes to it are ignored.')

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
        'it (' + F('run-alarm-history') + ').',
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
      'built-in login window (' + F('run-login') + '); `Login(name, password)`, `Logout()`, `ChangePassword(old, new)` and '
      '`ShowUserManager()` support custom login screens.')
    figure(doc, 'run-login', 'The login window opened by ShowLogin()', max_width_in=4.0)

    # ------------------------------------------------------------ 11
    H1('11  Recipes')
    P('A **recipe** is a named set of tag values that an operator saves and later loads again: the settings of a '
      'product, a grade or a batch size. Recipes are kept in **recipe books**. A book lists the tags its recipes '
      'hold:')
    bullets(doc, [
        '**Save/Load tags**: the values a recipe stores, usually memory tags that the operator edits on screen. '
        '`RecipeSave` reads them and `RecipeLoad` writes them.',
        '**Upload/Download tags** (optional): a second tag paired with each Save/Load tag, usually the PLC tag the '
        'value is meant for. `RecipeDownload` copies the Save/Load values to them and `RecipeUpload` copies them '
        'back.',
    ])
    P('Keeping the edited values in memory tags means the operator can prepare a recipe without disturbing the '
      'PLC, and send it with one Download when the process is ready.')

    H2('11.1  Recipe books')
    P('Open **HMI > Recipes** to define the books. LiquidWeighHMI has one, **Plasticizer Blends**, that holds the '
      'Batch screen\'s recipe: the total batch volume and the share of each of the three plasticizers.')
    figure(doc, 'recipes-dialog', 'HMI > Recipes: the Plasticizer Blends book and its tags', max_width_in=5.6)
    bullets(doc, [
        '**Add**, **Duplicate** and **Delete** manage the books; **Name** renames the selected one.',
        '**Upload/Download tags** adds the second column of tags.',
        '**Add tag** adds a row. The arrow buttons reorder the rows and **Remove** deletes one. A tag that is not in '
        'the Tag Dictionary is marked.',
        '**Import...** and **Export...** read and write books, with their starting recipes, as a JSON file, to copy '
        'them between projects.',
        '**OK** checks every book and saves them into the project. Validate also reports books whose tags no '
        'longer exist.',
    ])
    table(doc, 'The tags of the Plasticizer Blends book', [
        ['Save/Load tag', 'Upload/Download tag', 'Holds'],
        ['Edit_TotalVol', 'Rcp_TotalVol', 'Total batch volume, gal (PLC tag Recipe.TotalVol_gal)'],
        ['Edit_Pct0', 'Rcp_Pct0', 'Plasticizer #1, % by volume (Recipe.Pct[0])'],
        ['Edit_Pct1', 'Rcp_Pct1', 'Plasticizer #2, % by volume (Recipe.Pct[1])'],
        ['Edit_Pct2', 'Rcp_Pct2', 'Plasticizer #3, % by volume (Recipe.Pct[2])'],
    ])
    P('The Edit_ tags are retentive memory tags, so the recipe being edited survives a restart. The PLC works out '
      'each plasticizer\'s target weight from the volume, the percentages and the densities on the Setpoints '
      'screen.')
    P('A book can also carry **starting recipes**: recipes stored in the project, which a computer uses until it '
      'saves recipes of its own. LiquidWeighHMI ships with five.')
    figure(doc, 'recipes-dialog-recipes', 'The starting recipes of Plasticizer Blends, below its tags', max_width_in=5.6)
    table(doc, 'LiquidWeighHMI\'s starting recipes', [
        ['Recipe', 'Volume (gal)', 'Plasticizer #1 (%)', 'Plasticizer #2 (%)', 'Plasticizer #3 (%)'],
        ['Standard Blend', '10', '60', '25', '15'],
        ['High Plasticizer', '10', '70', '20', '10'],
        ['Hose Compound', '9', '45', '35', '20'],
        ['Low Odor', '8', '50', '50', '0'],
        ['Trial Batch', '4', '40', '30', '30'],
    ])

    H2('11.2  The Recipes screen')
    P('LiquidWeighHMI\'s **Recipes** screen, on the RECIPES button of the navigation bar, puts the recipe '
      'functions to work. It is built only from standard objects, links and scripts, so it can be copied into '
      'other applications.')
    figure(doc, 'run-recipes', 'The Recipes screen at Run: Standard Blend selected, loaded and downloaded to the PLC')
    bullets(doc, [
        '**Recipe book** (left): a Recipe List object showing the book\'s recipes. Touching one, or the arrow '
        'buttons in its heading, selects it by writing its name to the message tag `RecipeName`.',
        '**Recipe editor** (center): the recipe name, and for each value the editable Save/Load tag (Recipe column) '
        'beside its PLC tag (In the PLC column). The total and the green or red banner check that the percentages '
        'add up to 100 %. **Last action** shows the result of the last button.',
        '**Recipe actions** (right): one button for each recipe function, acting on the recipe named in '
        '`RecipeName`.',
    ])
    table(doc, 'The Recipes screen\'s buttons', [
        ['Button', 'Calls', 'Allowed for'],
        ['SELECT RECIPE...', 'ShowRecipeSelect, then RecipeLoad', 'Everyone'],
        ['LOAD', 'RecipeLoad: the recipe into the editor', 'Everyone'],
        ['SAVE', 'RecipeSave: the editor\'s values as the recipe', 'Maintenance and up'],
        ['DOWNLOAD TO PLC', 'RecipeDownload: the editor\'s values to the PLC', 'Operator and up, sequence idle'],
        ['UPLOAD FROM PLC', 'RecipeUpload: the PLC\'s values into the editor', 'Everyone'],
        ['DELETE', 'RecipeDelete, asking first', 'Maintenance and up'],
        ['EXPORT CSV...', 'RecipeExport', 'Everyone'],
        ['IMPORT CSV...', 'RecipeImport, saving under the name in the editor', 'Maintenance and up'],
        ['RENAME', 'RecipeRename to the name typed above it', 'Maintenance and up'],
    ])
    P('Each button is an Action Script. Where access is limited, an **Enable** link (for example '
      '`_AccessLevel >= 2000`) blocks touch, and fill and text color links gray the button out. SAVE reports its '
      'result in the Last action box:')
    code(doc, """
IF RecipeSave("Plasticizer Blends", RecipeName) THEN
    RecipeMsg = "Saved " + RecipeName;
ENDIF;
""")
    P('DOWNLOAD TO PLC is enabled by `_AccessLevel >= 1000 AND Seq_Step == 0`, so a recipe cannot change while a '
      'batch runs. DELETE passes 1 as the third argument, so the operator confirms first:')
    code(doc, """
IF RecipeDelete("Plasticizer Blends", RecipeName, 1) THEN
    RecipeMsg = "Deleted " + RecipeName;
    RecipeName = "";
ENDIF;
""")
    figure(doc, 'run-recipe-confirm', 'RecipeDelete with confirm: the operator answers before anything is deleted',
           max_width_in=4.0)

    H2('11.3  The Recipe List object')
    P('**Recipe List**, in the HMI palette, shows a book\'s recipes while the application runs. Select it and open '
      'the Animation tab to set it up:')
    figure(doc, 'recipe-list-panel', 'The Recipe List settings of the Recipes screen\'s list', max_width_in=2.4)
    table(doc, 'Recipe List settings', [
        ['Setting', 'Meaning'],
        ['Recipe book', 'The book listed'],
        ['Title', 'The heading; empty shows the book\'s name'],
        ['Selected recipe', 'A message tag. The highlighted row follows its value, and touching a row writes the '
         'row\'s name to it'],
        ['Select up, Select down', 'Discrete tags. Each change from 0 to 1 moves the selection up or down, for '
         'selection from PLC pushbuttons. With nothing selected, down selects the first recipe and up the last'],
        ['Up and down arrow buttons', 'Draws arrow buttons in the heading that move the selection the same way'],
    ])
    P('The list is sorted by name and updates as soon as a recipe is saved, renamed or deleted.')

    H2('11.4  Selecting a recipe in a window')
    P('`ShowRecipeSelect` opens a window listing a book\'s recipes, for screens without a Recipe List or for a '
      'popup choice. The operator touches a recipe, or uses the arrow buttons, then **Select**; the function returns '
      'the chosen name, or "" for **Cancel**. LiquidWeighHMI\'s SELECT RECIPE... button loads the chosen recipe '
      'into the editor:')
    code(doc, """
RecipePick = ShowRecipeSelect("Plasticizer Blends");
IF RecipePick <> "" THEN
    RecipeName = RecipePick;
    RecipeLoad("Plasticizer Blends", RecipeName);
    RecipeMsg = "Loaded " + RecipeName + " into the editor";
ENDIF;
""")
    figure(doc, 'run-recipe-select', 'The window opened by ShowRecipeSelect', max_width_in=4.0)
    bullets(doc, [
        'The script waits at ShowRecipeSelect until the operator chooses, then continues with the result. It must '
        'therefore be a statement on its own or the whole right side of an assignment, as above; Validate reports '
        'it inside an IF condition or an expression.',
        'The window is centered. `ShowRecipeSelect(book, x, y, w, h)` places it instead, at x, y with width w and '
        'height h in screen pixels, scaled with the screen.',
        'Assigning the result to a separate tag, as above, keeps the current selection when the operator cancels.',
    ])

    H2('11.5  When a recipe function fails')
    P('A recipe function that cannot do its job returns 0 and shows the **Recipe Error** window. The window names '
      'the call with its arguments and gives the reason, such as an empty recipe name, a recipe that does not '
      'exist, or PLC values with bad quality because the controller is not connected. Pressing SAVE with no recipe '
      'name gives:')
    figure(doc, 'run-recipe-error', 'The Recipe Error window', max_width_in=4.0)
    bullets(doc, [
        'The window opens after the script finishes, so the rest of the script still runs.',
        'Further failures before **OK** is pressed join the same window (up to 10, then "and n more"); a failure '
        'repeated by a While showing script is counted rather than repeated.',
        'The operator\'s own choices are not errors: Cancel in the select window, No when deleting and cancelling a '
        'file window return 0 or "" without one.',
        'Every failure is also written to the runtime log.',
    ])

    H2('11.6  Where recipes are kept')
    P('The project holds the books and their starting recipes. Recipes saved while running (RecipeSave, '
      'RecipeRename, RecipeDelete, RecipeImport) are kept on the computer running the application, book by book: '
      'once a computer saves a recipe in a book, its own list replaces that book\'s starting recipes there.')
    table(doc, 'Where saved recipes are kept', [
        ['Running in', 'File'],
        ['Append HMI Studio (Run)', 'recipes\\<project>.json in the Studio\'s settings folder'],
        ['A published application', '%APPDATA%\\<Product>\\recipes\\<Product>.json'],
        ['Append HMI Desktop, Append HMI Web', 'recipes\\<application>.json in their settings folder'],
    ])
    bullets(doc, [
        '**HMI > Clear Runtime Recipes** (with the Run stopped) forgets the recipes the Studio saved, so Run starts '
        'again from the starting recipes.',
        'In Append HMI Web all browsers share the server\'s recipes; one saved in one browser appears in the others '
        'within a few seconds.',
        'A recipe CSV file has a `#Recipe,<book>,<name>` line, then `Tag,Value` and one line per tag. Importing takes '
        'only the book\'s Save/Load tags and reports any others.',
    ])

    # ------------------------------------------------------------ 12
    H1('12  Testing')
    H2('12.1  Validate')
    P('**HMI > Validate Expressions** checks every link, script and PLC address and lists any problem with the '
      'page and object; click a problem to go to it. Validate before every Run and Publish.')
    figure(doc, 'validate', 'Validation of LiquidWeighHMI: no problems found', max_width_in=4.0)
    H2('12.2  Run')
    P('**HMI > Run** (F5) runs the application inside the Studio: the drawing is locked, the start-up windows '
      'open, and the application connects to its PLCs. A green RUNNING banner shows that the screen is live and '
      'that touching it operates real equipment. **HMI > Stop** returns to editing.')
    figure(doc, 'run-window', 'LiquidWeighHMI running in the Studio')
    P('The screens below were captured while LiquidWeighHMI ran against its controller, between batches.')
    figure(doc, 'run-process', 'Process screen: tank and hopper levels, valve states, sequence and active alarms')
    figure(doc, 'run-faceplate', 'The Device Faceplate showing SV-001: mode, commands, interlocks and fault reset')
    figure(doc, 'run-batch', 'Batch screen: recipe, sequence control, start permissives and batch progress')
    figure(doc, 'run-setpoints', 'Setpoints screen: materials, weighing, pressure and timing')
    figure(doc, 'run-devices', 'Devices screen: every valve and pump with mode, state and faceplate')
    figure(doc, 'run-supply', 'Supply screen: plasticizer tank, refill station and totes')
    figure(doc, 'run-alarm-history', 'Alarm History screen')
    note(doc, 'HMI > Runtime Log lists connections, device errors, writes and script errors from the run.')

    # ------------------------------------------------------------ 13
    H1('13  Deploying')
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
    H2('13.1  Publish')
    P('**HMI > Publish** builds a Windows installer from the open project. Give the product name, version and '
      'publisher, choose an icon, whether it installs for all users, and whether it adds a desktop shortcut or '
      'starts with Windows. Run the Setup.exe on the target PC; the application then starts full screen as set '
      'in Application Settings.')
    figure(doc, 'publish', 'The Publish dialog', max_width_in=4.0)
    H2('13.2  Append HMI Desktop')
    P('Append HMI Desktop runs .ahmi files without the editor. Choose an application, then **Run now**, create a '
      'desktop shortcut or menu entry, or tick **Run this application automatically when I log in**. From the '
      'command line: `append-hmi-desktop LiquidWeighHMI.ahmi`.')
    figure(doc, 'desktop-launcher', 'The Append HMI Desktop launcher with LiquidWeighHMI selected', max_width_in=4.2)
    H2('13.3  Append HMI Web')
    P('Append HMI Web serves an application to web browsers on the plant network. Choose the application and a port '
      '(8480 by default), then **Start web server**; the launcher shows the link to share, with a **Copy** button. '
      'If the port is already in use on the PC (by another web server, or another application being served), '
      'choose another. In the browser, the view button in the corner fits the screen to the window, maximizes it, '
      'or shows it at its original size with scroll bars.')
    P('The **Running** list shows every Append HMI Web server on the PC, including ones started in the background '
      'from the command line (`--headless`). Each has **Restart**, which starts it again with the same settings and '
      'reads the project file afresh (do this after saving changes in Studio; browsers reload by themselves), and '
      '**Stop**.')
    figure(doc, 'web-launcher', 'The Append HMI Web launcher: LiquidWeighHMI served on port 8481 to one browser, and a '
        'second application running in the background', max_width_in=4.2)
    figure(doc, 'web-browser', 'LiquidWeighHMI in a web browser, with the view menu open')

    # ------------------------------------------------------------ 14
    H1('14  Tips and troubleshooting')
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
        ['A recipe button does nothing', 'Its Enable condition is false (access level, or the sequence is running), '
         'or the function failed: the Recipe Error window and HMI > Runtime Log give the reason'],
        ['Run shows old recipes', 'Recipes saved on this computer replace the starting ones. Use HMI > Clear Runtime '
         'Recipes to start from the project\'s recipes again'],
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

    if save:
        doc.save(OUT)
        print('Wrote ' + os.path.relpath(OUT))


if __name__ == '__main__':
    # Twice: the first pass numbers the figures, the second refers to them
    build(save=False)
    _known_figures.update(_figure_names)
    _figures[0] = 0
    _tables[0] = 0
    build()
    enforce(OUT)
