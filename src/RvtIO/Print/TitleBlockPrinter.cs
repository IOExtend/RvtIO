using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>
/// The title block strip on the right of a sheet, drawn from the sheet's own fields: a logo area, the project area, a
/// revision table and the sheet information (name, number, date, drawn by, checked by). The graphic of the title block
/// family itself is not decoded, so this strip stands in for it; every value in it is read from the file.
/// </summary>
internal static class TitleBlockPrinter
{
    /// <summary>Draws the strip in the page margins: <paramref name="widthMillimetres"/> wide, from the lower border to
    /// the upper one.</summary>
    internal static void Draw(PdfPage page, RvtDocument document, Element sheet, double widthMillimetres,
        double marginMillimetres, string scale)
    {
        double pageWidth = page.Size.WidthMillimetres, pageHeight = page.Size.HeightMillimetres;
        double left = pageWidth - marginMillimetres - widthMillimetres;
        double bottom = marginMillimetres, top = pageHeight - marginMillimetres;
        double height = top - bottom;
        page.DrawRectangle(left, bottom, widthMillimetres, height, 0.35);

        string number = sheet.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string ?? "";
        string name = sheet.LookupParameter(BuiltInParameter.SHEET_NAME)?.Value as string ?? "";
        string date = sheet.LookupParameter(BuiltInParameter.SHEET_ISSUE_DATE)?.Value as string ?? "";
        string drawn = sheet.LookupParameter(BuiltInParameter.SHEET_DRAWN_BY)?.Value as string ?? "";
        string checkedBy = sheet.LookupParameter(BuiltInParameter.SHEET_CHECKED_BY)?.Value as string ?? "";
        string project = document.ProjectInformation?.Name ?? "";
        string projectNumber = document.ProjectInformation?.Number ?? "";

        // Logo area at the top, then the project area.
        double logoHeight = Math.Min(40, height * 0.15);
        double projectHeight = Math.Min(55, height * 0.2);
        double logoBottom = top - logoHeight;
        double projectBottom = logoBottom - projectHeight;
        page.DrawLine(left, logoBottom, left + widthMillimetres, logoBottom, 0.25);
        page.DrawLine(left, projectBottom, left + widthMillimetres, projectBottom, 0.25);
        if (project.Length > 0) page.DrawText(project, left + 3, logoBottom - 8, 4);
        else page.DrawText("Project", left + 3, logoBottom - 8, 3, PdfTextAlignment.Left, 0.45);
        if (projectNumber.Length > 0) page.DrawText(projectNumber, left + 3, logoBottom - 15, 3.5);

        // The sheet information at the bottom: name, number, scale, date, drawn by, checked by.
        double infoHeight = Math.Min(80, height * 0.32);
        double infoTop = bottom + infoHeight;
        page.DrawLine(left, infoTop, left + widthMillimetres, infoTop, 0.25);
        double rowHeight = infoHeight / 6;
        for (int i = 0; i < 6; i++)
        {
            double rowY = infoTop - (i + 1) * rowHeight;
            if (i > 0) page.DrawLine(left, rowY, left + widthMillimetres, rowY, 0.15);
            string label = i switch
            {
                0 => "Sheet Name", 1 => "Sheet Number", 2 => "Scale", 3 => "Issue Date", 4 => "Drawn By",
                _ => "Checked By",
            };
            string value = i switch
            {
                0 => name, 1 => number, 2 => scale, 3 => date, 4 => drawn, _ => checkedBy,
            };
            page.DrawText(label, left + 3, rowY + rowHeight - 4, 2.2, PdfTextAlignment.Left, 0.45);
            page.DrawText(value, left + 3, rowY + 2, i == 1 ? 6 : 3.2);
        }

        // The revision table between the project area and the sheet information.
        double tableTop = projectBottom - 4;
        double tableBottom = infoTop + 2;
        double row = Math.Min(8, (tableTop - tableBottom) / 9);
        double noWidth = 14, dateWidth = 32;
        page.DrawText("No.", left + 2, tableTop - 3, 2.2, PdfTextAlignment.Left, 0.45);
        page.DrawText("Description", left + noWidth + 2, tableTop - 3, 2.2, PdfTextAlignment.Left, 0.45);
        page.DrawText("Date", left + widthMillimetres - dateWidth + 2, tableTop - 3, 2.2,
            PdfTextAlignment.Left, 0.45);
        page.DrawLine(left, tableTop - 4, left + widthMillimetres, tableTop - 4, 0.2);
        for (int i = 0; i < 8; i++)
        {
            double y = tableTop - 4 - (i + 1) * row;
            if (y < tableBottom) break;
            page.DrawLine(left, y, left + widthMillimetres, y, 0.15);
        }
        page.DrawLine(left + noWidth, tableTop - 4, left + noWidth, tableBottom, 0.15);
        page.DrawLine(left + widthMillimetres - dateWidth, tableTop - 4, left + widthMillimetres - dateWidth,
            tableBottom, 0.15);
    }
}
