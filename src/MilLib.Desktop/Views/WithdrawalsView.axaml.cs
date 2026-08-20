using Avalonia.Controls;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;
using QuestPDF.Fluent;

namespace MilLib.Desktop.Views;

public partial class WithdrawalsView : UserControl
{
    public WithdrawalsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Watch();

        Watch();
    }

    private void Watch()
    {
        if (DataContext is not WithdrawalsViewModel model)
        {
            return;
        }

        model.PrintCertificate -= CertificateAsync;
        model.PrintCertificate += CertificateAsync;

        model.PrintRegister -= RegisterAsync;
        model.PrintRegister += RegisterAsync;
    }

    private async Task CertificateAsync(
        Withdrawal withdrawal, string by, IReadOnlyList<Condemned> books)
    {
        var document = new CondemnationDocument(
            Letterheads.Current(), withdrawal, by, books, Session.Preferences.CurrencySymbol);

        await Documents.SaveAsync(this, "Save the certificate of condemnation",
            $"Condemnation {withdrawal.WithdrawalNo}.pdf",
            path => document.GeneratePdf(path));
    }

    private async Task RegisterAsync(Report register)
    {
        var document = new ReportDocument(Letterheads.Current(), register);

        await Documents.SaveAsync(this, "Save the condemnation register",
            $"Condemnation Register {DateTime.Now:yyyy-MM-dd}.pdf",
            path => document.GeneratePdf(path));
    }
}
