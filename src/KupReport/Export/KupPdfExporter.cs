using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using KupReport.Reporting;
using QuestPDF.Drawing;
using QuestPDF.Elements;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace KupReport.Export;

/// <summary>
/// Generates the bilingual KUP works-registration PDF report, replicating the
/// original LaTeX template (Latin Modern fonts, layout, sizes and colors).
/// </summary>
public static class KupPdfExporter
{
    private const string FontFamily = "CMU Serif";
    private const string Blank = "________________";

    // LaTeX 'blue' used by the template for headings and hyperlinks.
    private static readonly Color LatexBlue = Color.FromRGB(0, 0, 255);

    private static readonly byte[] Logo;

    static KupPdfExporter()
    {
        // Resolve QuestPDF's native libraries from embedded resources so the
        // published application stays a single file.
        NativeLibrary.SetDllImportResolver(typeof(QuestPDF.Settings).Assembly, ResolveNativeLibrary);

        QuestPDF.Settings.License = LicenseType.Community;

        foreach (var font in (string[])["cmunrm", "cmunbx", "cmunti", "cmunbi"])
        {
            using var stream = OpenResource($"KupReport.Assets.fonts.{font}.ttf");
            FontManager.RegisterFont(stream);
        }

        using var logo = OpenResource("KupReport.Assets.accuris-logo.png");
        using var buffer = new MemoryStream();
        logo.CopyTo(buffer);
        Logo = buffer.ToArray();
    }

    private static Stream OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Missing embedded resource: {name}");

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var fileName = GetNativeLibraryFileName(libraryName);
        if (fileName is null)
            return IntPtr.Zero;

        using var resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"KupReport.Native.{fileName}");

        // Not one of ours - let the default loader handle it.
        if (resource is null)
            return IntPtr.Zero;

        var version = typeof(QuestPDF.Settings).Assembly.GetName().Version?.ToString() ?? "0";
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kup-report", "native", version,
            $"{GetPlatformName()}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}");
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $"{fileName}.{Environment.ProcessId}.tmp");
            using (var output = File.Create(temp))
                resource.CopyTo(output);

            try
            {
                File.Move(temp, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process extracted it concurrently.
                File.Delete(temp);
            }
        }

        return NativeLibrary.Load(path);
    }

    private static string? GetNativeLibraryFileName(string libraryName)
    {
        var baseName = Path.GetFileNameWithoutExtension(libraryName);
        if (baseName.StartsWith("lib", StringComparison.Ordinal))
            baseName = baseName[3..];

        return GetPlatformName() switch
        {
            "win" => $"{baseName}.dll",
            "osx" => $"lib{baseName}.dylib",
            "linux" => $"lib{baseName}.so",
            _ => null,
        };
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows())
            return "win";
        if (OperatingSystem.IsMacOS())
            return "osx";
        if (OperatingSystem.IsLinux())
            return "linux";

        return "unknown";
    }

    public static void Export(MonthlyKupReport report, ReportIdentity identity, string path)
    {
        Document.Create(document =>
        {
            document.Page(page =>
            {
                ConfigurePage(page);

                page.Content().Column(column =>
                {
                    // Pages 1..n: header, works table, statement.
                    column.Item().Width(6.9f, Unit.Centimetre).Image(Logo);

                    column.Item().PaddingTop(18).Text(
                            "FORMULARZ RAPORTU REJESTRACJI UTWORÓW DOTYCZĄCY MIESIĘCZNEGO OKRESU " +
                            "ROZLICZENIOWEGO / WORKS REGISTRATION REPORT FORM FOR THE MONTHLY BILLING PERIOD")
                        .FontSize(14.4f).Bold().FontColor(LatexBlue);

                    column.Item().PaddingTop(12).Element(c => MetaSection(c, report, identity));

                    column.Item().PaddingTop(16).Text("SPECYFIKACJA STWORZONYCH UTWORÓW")
                        .FontSize(14.4f).Bold();

                    column.Item().PaddingTop(10).Element(c => WorksTable(c, report));

                    column.Item().PaddingTop(8).Element(FootnotesSection);

                    column.Item().PaddingTop(14).Element(StatementSection);

                    // Last page: report acceptance for the controller.
                    column.Item().PageBreak();

                    column.Item().Text("PRZYJĘCIE RAPORTU REJESTRACJI UTWORÓW")
                        .FontSize(14.4f).Bold();

                    column.Item().PaddingTop(14).Element(c => Checkbox(c, "Raport przyjęty / ", "Report accepted"));
                    column.Item().PaddingTop(10).Element(c => Checkbox(c, "Raport nieprzyjęty / ", "Report not accepted"));

                    column.Item().PaddingTop(56).Text("Uwagi Kontrolera do Raportu lub poszczególnych utworów:")
                        .FontSize(12).Bold();
                });

                // Signatures are bottom-anchored like LaTeX \vfill: the author signs the
                // last page of the report body, the controller signs the acceptance page.
                page.Footer().Dynamic(new SignatureFooter());
            });
        }).GeneratePdf(path);
    }

    private static void ConfigurePage(PageDescriptor page)
    {
        page.Size(PageSizes.Letter);
        page.MarginHorizontal(1, Unit.Centimetre);
        page.MarginVertical(2, Unit.Centimetre);
        page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(10));
    }

    private static void MetaSection(IContainer container, MonthlyKupReport report, ReportIdentity identity)
    {
        container.Column(column =>
        {
            column.Spacing(2);
            MetaLine(column, "Raport za Okres Rozliczeniowy (miesiąc) / ", "Period covered by the Report (month)",
                report.To.ToString("MMMM", CultureInfo.InvariantCulture));
            MetaLine(column, "Imię i nazwisko Autora / ", "Author's full name", OrBlank(identity.AuthorName));
            MetaLine(column, "Stanowisko Autora / ", "Author's job title", OrBlank(identity.AuthorTitle));
            MetaLine(column, "Imię i nazwisko Kontrolera / ", "Controller's full name", OrBlank(identity.ManagerName));
            MetaLine(column, "Stanowisko Kontrolera / ", "Controller's Job title", OrBlank(identity.ManagerTitle));
            MetaLine(column, "Liczba dni roboczych w Okresie Rozliczeniowym / ",
                "Number of working days in the Period", $"{report.WorkingDays} days");
            MetaLine(column, "Nieobecności Autora / ", "Author's days of absence (working days)",
                $"{report.VacationDays} days");
        });
    }

    private static string OrBlank(string value) => value.Length > 0 ? value : Blank;

    private static void MetaLine(ColumnDescriptor column, string polish, string english, string value) =>
        column.Item().Text(text =>
        {
            text.DefaultTextStyle(style => style.FontSize(9));
            text.Span(polish);
            text.Span(english).Italic();
            text.Span(": ");
            text.Span(value).Bold();
        });

    private static void WorksTable(IContainer container, MonthlyKupReport report)
    {
        container.Table(table =>
        {
            // Template column widths: 0.8cm | 5cm | 5cm | 2.3cm | 1.8cm | 2cm, stretched to full width.
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(0.8f);
                columns.RelativeColumn(5f);
                columns.RelativeColumn(5f);
                columns.RelativeColumn(2.3f);
                columns.RelativeColumn(1.8f);
                columns.RelativeColumn(2f);
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Lp.", "", "(No.)");
                HeaderCell(header.Cell(), "Nazwa Utworu i opis Utworu", "*", "(Created work title and short description)");
                HeaderCell(header.Cell(), "Sygnatura/ Ścieżka dostępu", "**", "(Signature/Access path)");
                HeaderCell(header.Cell(), "Liczba godzin poświęconych na stworzenie utworu", "",
                    "(number of hours spent on the creation of the work )");
                HeaderCell(header.Cell(), "Data powstania utworu", "", "(date of creation of the work)");
                HeaderCell(header.Cell(), "Osoba zlecająca wykonanie Utworu", "", "(Person commissioning the Work)");
            });

            var index = 0;
            foreach (var entry in report.Entries
                         .Where(e => e.IsMerged && e.KupHours is not null)
                         .OrderByDescending(e => e.CreatedAt))
            {
                index++;
                BodyCell(table.Cell()).Text(index.ToString()).FontSize(10);
                BodyCell(table.Cell()).Text(text =>
                {
                    if (entry.WorkItem is { } workItem)
                    {
                        text.Hyperlink(workItem.Label, workItem.Url).FontColor(LatexBlue);
                        text.Span($": {workItem.Title}");
                    }
                    else
                    {
                        text.Span(entry.Title);
                    }
                });
                BodyCell(table.Cell()).Text(text =>
                {
                    text.Hyperlink($"PR {entry.Number}", entry.Url).FontColor(LatexBlue);
                    text.Span($": {entry.Title}");
                });
                BodyCell(table.Cell()).Text(entry.KupHours!.Value.ToString("0.#", CultureInfo.InvariantCulture));
                BodyCell(table.Cell()).Text(entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"));
                BodyCell(table.Cell()).Text(text =>
                {
                    if (entry.WorkItem?.OwnerName is not { Length: > 0 } owner)
                        return;

                    if (entry.WorkItem.OwnerEmail is { Length: > 0 } email)
                        text.Hyperlink(owner, $"mailto:{email}").FontColor(LatexBlue);
                    else
                        text.Span(owner);
                });
            }
        });
    }

    private static void HeaderCell(IContainer cell, string polish, string footnoteMark, string english) =>
        cell.Border(0.75f).Padding(5).Column(column =>
        {
            column.Item().Text(text =>
            {
                text.Span(polish).FontSize(9).Bold();
                if (footnoteMark.Length > 0)
                    text.Span(footnoteMark).FontSize(9).Bold().Superscript();
                text.Span("/").FontSize(9).Bold();
            });
            column.Item().Text(english).FontSize(8).Italic();
        });

    private static IContainer BodyCell(IContainer cell) =>
        cell.Border(0.5f).Padding(5).DefaultTextStyle(style => style.FontSize(9));

    private static void FootnotesSection(IContainer container) =>
        container.Column(column =>
        {
            column.Item().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8));
                text.Span("* informacja pozwalająca na identyfikację utworu / ");
                text.Span("information enabling the identification of the work").Italic();
                text.Span(";");
            });
            column.Item().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8));
                text.Span("** wskazanie sygnatury każdego z utworów nadanej zgodnie z zasadami obowiązującymi " +
                          "w Spółce lub ścieżki dostępu do każdego utworu zapisanego w zasobach sieciowych Spółki " +
                          "zgodnie z przyjętymi zasadami / ");
                text.Span("indication of the signature of each work assigned in accordance with the rules " +
                          "applicable in the Company or the access path to each work saved in the Company's " +
                          "network resources in accordance with the Company rules").Italic();
                text.Span(".");
            });
        });

    private static void StatementSection(IContainer container) =>
        container.Column(column =>
        {
            column.Spacing(10);
            column.Item().Text(text =>
            {
                text.Justify();
                text.Span("OŚWIADCZENIE").Bold();
                text.Span(": Potwierdzam, że Utwory dotyczą dziedziny wchodzącej w zakres działalności " +
                          "określonej w art. 22 ust. 9b Ustawy o PIT. Niniejszym potwierdzam, że zgłoszone " +
                          "przeze mnie Utwory, stanowiące wynik mojej działalności twórczej o indywidualnym " +
                          "charakterze chronione przepisami Ustawy, powstały w ramach mojej działalności " +
                          "twórczej w zakresie programów komputerowych w rozumieniu Ustawy o PIT. Wyrażam " +
                          "zgodę na anonimową publikację utworów, których jestem autorem i które zostały " +
                          "wyszczególnione powyżej oraz na ich dalsze opracowywanie przez Spółkę. Oświadczam " +
                          "również, że wymienione powyżej utwory nie stanowią plagiatu.");
            });
            column.Item().Text(text =>
            {
                text.Justify();
                text.DefaultTextStyle(style => style.FontSize(9).Italic());
                text.Span("STATEMENT").Bold();
                text.Span(": I confirm that the Works concern a field falling within the scope of activities " +
                          "specified in Art. 22 section 9b of the Personal Income Tax Act. I hereby confirm " +
                          "that the Works submitted by me, which are the result of my individual creative " +
                          "activity protected by the provisions of the Act, were created as part of my creative " +
                          "activity in the field of computer programs within the meaning of the Personal Income " +
                          "Tax Act. I consent to the anonymous publication of the works of which I am the " +
                          "Author, and which are listed above, and to their further development by the Company. " +
                          "I also declare that the works mentioned above do not constitute plagiarism.");
            });
        });

    /// <summary>
    /// Renders the author signature on the last page of the report body and the
    /// controller signature on the acceptance page (the document's last page).
    /// The footer always occupies the same height so pagination stays stable
    /// across QuestPDF's layout passes.
    /// </summary>
    private sealed class SignatureFooter : IDynamicComponent
    {
        private const float Height = 60;

        public DynamicComponentComposeResult Compose(DynamicContext context)
        {
            var content = context.CreateElement(element =>
            {
                var slot = element.Height(Height).AlignBottom();

                if (context.PageNumber == context.TotalPages - 1)
                    slot.Element(c => SignatureLine(
                        c, "Podpis autora / ", "(Signature of author)", "Data, miejsce / ", "(Date, city)"));
                else if (context.PageNumber == context.TotalPages)
                    slot.Element(c => SignatureLine(
                        c, "Podpis Kontrolera / ", "(Controller's Signature)", "Data, miejsce / ", "(Date, city)"));
            });

            return new DynamicComponentComposeResult { Content = content, HasMoreContent = false };
        }
    }

    private static void SignatureLine(
        IContainer container, string leftPolish, string leftEnglish, string rightPolish, string rightEnglish) =>
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("........................................................");
                column.Item().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(9));
                    text.Span(leftPolish);
                    text.Span(leftEnglish).Italic();
                });
            });
            row.RelativeItem().AlignRight().Column(column =>
            {
                column.Item().Text("..........................................");
                column.Item().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(9));
                    text.Span(rightPolish);
                    text.Span(rightEnglish).Italic();
                });
            });
        });

    private static void Checkbox(IContainer container, string polish, string english) =>
        container.Row(row =>
        {
            row.AutoItem().Border(1.2f).Width(14.2f).Height(14.2f);
            row.AutoItem().PaddingLeft(6).AlignMiddle().Text(text =>
            {
                text.Span(polish);
                text.Span(english).Italic();
            });
        });
}
