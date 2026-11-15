using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>
/// The title block strip on the right of a sheet: a logo area, the consultant table, the revision table, the project
/// area and the sheet information (name, number, scale, issue date, drawn by, checked by). The graphic of the title
/// block family itself is not decoded, so the strip stands in for it; every value in it is read from the file and the
/// empty fields stay empty.
/// </summary>
internal static class TitleBlockPrinter
{
    /// <summary>Draws the strip in the page margins: <paramref name="widthMillimetres"/> wide, from the lower border to
    /// the upper one.</summary>
    internal static void Draw(PdfPage page, RvtDocument document, Element sheet, double widthMillimetres,
        double marginMillimetres, string scale, PdfRasterImage? logo = null)
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

        // The logo area at the top, then the consultant table.
        double logoHeight = Math.Min(35, height * 0.13);
        double logoBottom = top - logoHeight;
        if (logo != null)
        {
            // The logo the title block family places, fitted into its area with its aspect preserved.
            double available = widthMillimetres - 6;
            double logoWidth = Math.Min(available, logoHeight * 0.55 * logo.Width / logo.Height);
            double logoDrawHeight = logoWidth * logo.Height / logo.Width;
            if (logoDrawHeight > logoHeight - 4)
            {
                logoDrawHeight = logoHeight - 4;
                logoWidth = logoDrawHeight * logo.Width / logo.Height;
            }
            page.DrawImage(logo, left + (widthMillimetres - logoWidth) / 2, logoBottom + (logoHeight - logoDrawHeight) / 2,
                logoWidth, logoDrawHeight);
        }
        page.DrawLine(left, logoBottom, left + widthMillimetres, logoBottom, 0.25);

        double consultantHeight = Math.Min(55, height * 0.19);
        double consultantBottom = logoBottom - consultantHeight;
        page.DrawLine(left, consultantBottom, left + widthMillimetres, consultantBottom, 0.25);
        DrawConsultantTable(page, left, consultantBottom, widthMillimetres, consultantHeight);

        // The sheet information at the bottom: name, number, scale, date, drawn by, checked by.
        double infoHeight = Math.Min(80, height * 0.27);
        double infoTop = bottom + infoHeight;
        page.DrawLine(left, infoTop, left + widthMillimetres, infoTop, 0.25);
        DrawSheetInfo(page, left, bottom, widthMillimetres, infoHeight, name, number, scale, date, drawn, checkedBy);

        // The project area above the sheet information.
        double projectHeight = Math.Min(40, height * 0.14);
        double projectTop = infoTop + projectHeight;
        page.DrawLine(left, projectTop, left + widthMillimetres, projectTop, 0.25);
        if (project.Length > 0) page.DrawText(project, left + 3, infoTop + projectHeight - 9, 4);
        else page.DrawText("Project", left + 3, infoTop + projectHeight - 9, 2.2, PdfTextAlignment.Left, 0.45);
        if (projectNumber.Length > 0) page.DrawText(projectNumber, left + 3, infoTop + 3, 3.5);

        // The revision table fills what is left between the consultant table and the project area.
        DrawRevisionTable(page, left, projectTop + 4, widthMillimetres, consultantBottom - 4 - projectTop - 4);
    }

    private static void DrawConsultantTable(PdfPage page, double left, double bottom, double width, double height)
    {
        double half = width / 2;
        page.DrawLine(left + half, bottom, left + half, bottom + height, 0.2);
        string[] labels = ["Consultant", "Address", "Phone", "Fax", "e-mail"];
        double row = height / (labels.Length + 1);
        for (int i = 0; i < labels.Length; i++)
        {
            double y = bottom + height - (i + 1) * row - 3;
            page.DrawText(labels[i], left + 3, y, 1.9, PdfTextAlignment.Left, 0.45);
            page.DrawText(labels[i], left + half + 2, y, 1.9, PdfTextAlignment.Left, 0.45);
            if (i > 0) page.DrawLine(left, y + 3.5, left + width, y + 3.5, 0.1);
        }
    }

    private static void DrawRevisionTable(PdfPage page, double left, double top, double width, double height)
    {
        if (height < 12) return;
        double noWidth = 14, dateWidth = 30;
        page.DrawText("No.", left + 2, top - 3, 2, PdfTextAlignment.Left, 0.45);
        page.DrawText("Description", left + noWidth + 2, top - 3, 2, PdfTextAlignment.Left, 0.45);
        page.DrawText("Date", left + width - dateWidth + 2, top - 3, 2, PdfTextAlignment.Left, 0.45);
        page.DrawLine(left, top - 4, left + width, top - 4, 0.2);
        double row = Math.Min(7, (height - 4) / 10);
        for (int i = 0; i < 10; i++)
        {
            double y = top - 4 - (i + 1) * row;
            if (y < top - height) break;
            page.DrawLine(left, y, left + width, y, 0.1);
        }
        page.DrawLine(left + noWidth, top - 4, left + noWidth, top - height, 0.12);
        page.DrawLine(left + width - dateWidth, top - 4, left + width - dateWidth, top - height, 0.12);
    }

    private static void DrawSheetInfo(PdfPage page, double left, double bottom, double width, double height,
        string name, string number, string scale, string date, string drawn, string checkedBy)
    {
        double rowHeight = height / 6;
        for (int i = 0; i < 6; i++)
        {
            double rowY = bottom + height - (i + 1) * rowHeight;
            if (i > 0) page.DrawLine(left, rowY, left + width, rowY, 0.15);
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
    }
}
