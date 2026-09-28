#!/usr/bin/env python3
"""Finishes the User Manual with LibreOffice: fills in the table of contents
(and page numbers), then writes the PDF, and the .docx with the contents
filled in when --docx is given.

    python3 doc/manual/finish.py [--docx]

Uses LibreOffice's Python bridge (uno), with a private headless soffice.
"""

import os
import subprocess
import sys
import tempfile
import time

import uno
from com.sun.star.beans import PropertyValue

HERE = os.path.dirname(os.path.abspath(__file__))
DOCX = os.path.abspath(os.path.join(HERE, '..', 'Append-HMI-Studio-User-Manual.docx'))
PDF = DOCX[:-5] + '.pdf'


def prop(name, value):
    p = PropertyValue()
    p.Name = name
    p.Value = value

    return p


def main():
    profile = tempfile.mkdtemp(prefix='hmi-manual-lo-')
    port = 2211
    office = subprocess.Popen(['soffice', '--headless', '--invisible', '--norestore', '--nologo',
        '-env:UserInstallation=file://' + profile,
        '--accept=socket,host=127.0.0.1,port=%d;urp;' % port],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    try:
        local = uno.getComponentContext()
        resolver = local.ServiceManager.createInstanceWithContext('com.sun.star.bridge.UnoUrlResolver', local)
        ctx = None

        for _ in range(60):
            try:
                ctx = resolver.resolve('uno:socket,host=127.0.0.1,port=%d;urp;StarOffice.ComponentContext' % port)
                break
            except Exception:
                time.sleep(0.5)

        if ctx is None:
            raise SystemExit('LibreOffice did not start')

        desktop = ctx.ServiceManager.createInstanceWithContext('com.sun.star.frame.Desktop', ctx)
        doc = desktop.loadComponentFromURL(uno.systemPathToFileUrl(DOCX), '_blank', 0, (prop('Hidden', True),))

        # Twice: the contents' own length can move later headings
        for _ in range(2):
            indexes = doc.getDocumentIndexes()

            for i in range(indexes.getCount()):
                indexes.getByIndex(i).update()

            doc.getTextFields().refresh()

        doc.storeToURL(uno.systemPathToFileUrl(PDF), (prop('FilterName', 'writer_pdf_Export'),
            prop('FilterData', uno.Any('[]com.sun.star.beans.PropertyValue', tuple([
                prop('UseTaggedPDF', True), prop('ExportBookmarks', True), prop('Quality', 90)]))),))
        print('Wrote ' + os.path.relpath(PDF))

        if '--docx' in sys.argv:
            doc.storeToURL(uno.systemPathToFileUrl(DOCX), (prop('FilterName', 'MS Word 2007 XML'),))
            print('Updated ' + os.path.relpath(DOCX))

        doc.close(True)
    finally:
        office.terminate()

        try:
            office.wait(10)
        except Exception:
            office.kill()


if __name__ == '__main__':
    main()
