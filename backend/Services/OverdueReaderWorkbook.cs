using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Project.Services;

public static class OverdueReaderWorkbook
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static readonly IReadOnlyList<string> Headers =
        ["Mã thẻ", "Họ tên", "Số điện thoại", "Số cuốn quá hạn", "Số ngày trễ dài nhất", "Tổng phí đang nợ"];

    public static byte[] Create(IReadOnlyList<OverdueReaderExportRow> rows)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Write(string path, XDocument doc)
            {
                using var output = zip.CreateEntry(path).Open();
                doc.Save(output);
            }
            XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
            Write("[Content_Types].xml", new(new XElement(types + "Types",
                new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new[] { ("/xl/workbook.xml", "sheet.main"), ("/xl/worksheets/sheet1.xml", "worksheet"), ("/xl/styles.xml", "styles") }
                    .Select(p => new XElement(types + "Override", new XAttribute("PartName", p.Item1),
                        new XAttribute("ContentType", $"application/vnd.openxmlformats-officedocument.spreadsheetml.{p.Item2}+xml"))))));
            XDocument Relations(params (string Id, string Kind, string Target)[] items)
            {
                XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
                return new(new XElement(ns + "Relationships", items.Select(i => new XElement(ns + "Relationship",
                    new XAttribute("Id", i.Id), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + i.Kind), new XAttribute("Target", i.Target)))));
            }
            Write("_rels/.rels", Relations(("rId1", "officeDocument", "xl/workbook.xml")));
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            Write("xl/workbook.xml", new(new XElement(S + "workbook", new XElement(S + "sheets",
                new XElement(S + "sheet", new XAttribute("name", "Bạn đọc"), new XAttribute("sheetId", 1), new XAttribute(rel + "id", "rId1"))))));
            Write("xl/_rels/workbook.xml.rels", Relations(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "styles", "styles.xml")));
            Write("xl/styles.xml", XDocument.Parse("""
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.00 &quot;₫&quot;"/></numFmts>
                  <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                  <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                  <borders count="1"><border/></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
                </styleSheet>
                """));
            XElement Text(string cell, string value, int style = 0) => new(S + "c", new XAttribute("r", cell), new XAttribute("t", "inlineStr"), new XAttribute("s", style),
                new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
            XElement Number(string cell, decimal value, int style = 0) => new(S + "c", new XAttribute("r", cell), new XAttribute("s", style), new XElement(S + "v", value.ToString(CultureInfo.InvariantCulture)));
            var data = new XElement(S + "sheetData", new XElement(S + "row", new XAttribute("r", 1), Headers.Select((h, i) => Text($"{(char)('A' + i)}1", h, 1))));
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; var n = i + 2;
                data.Add(new XElement(S + "row", new XAttribute("r", n), Text($"A{n}", r.CardCode), Text($"B{n}", r.FullName), Text($"C{n}", r.PhoneNumber), Number($"D{n}", r.OverdueCount), Number($"E{n}", r.MaxDaysOverdue), Number($"F{n}", r.Debt, 2)));
            }
            Write("xl/worksheets/sheet1.xml", new(new XElement(S + "worksheet",
                new XElement(S + "cols", new[] { 22, 35, 22, 23, 28, 26 }.Select((w, i) => new XElement(S + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", w), new XAttribute("customWidth", 1)))), data)));
        }
        return stream.ToArray();
    }
}
