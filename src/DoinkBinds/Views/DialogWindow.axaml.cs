using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace DoinkBinds.Views;

/// <summary>Small reusable modal dialog. Result: the entered text / chosen item / "ok", or null if cancelled.</summary>
public partial class DialogWindow : Window
{
    public enum Kind { Message, Confirm, Prompt, Choose }

    public DialogWindow()
    {
        InitializeComponent();
    }

    public DialogWindow(Kind kind, string title, string message, string confirmText, bool danger,
        string? initialText = null, IReadOnlyList<string>? options = null, string? selected = null) : this()
    {
        Title = title;
        Heading.Text = title;
        Message.Text = message;
        Message.IsVisible = !string.IsNullOrWhiteSpace(message);
        OkButton.Content = confirmText;
        if (danger)
        {
            OkButton.Classes.Remove("primary");
            OkButton.Classes.Add("danger");
        }

        CancelButton.IsVisible = kind != Kind.Message;
        CancelButton.Click += (_, _) => Close(null);

        switch (kind)
        {
            case Kind.Prompt:
                Input.IsVisible = true;
                Input.Text = initialText ?? "";
                Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    Input.Focus();
                    Input.SelectAll();
                });
                OkButton.Click += (_, _) => Close(Input.Text);
                break;

            case Kind.Choose:
                Choices.IsVisible = true;
                Choices.ItemsSource = options;
                Choices.SelectedItem = selected ?? options?.FirstOrDefault();
                Choices.DoubleTapped += (_, _) => Close(Choices.SelectedItem as string);
                OkButton.Click += (_, _) => Close(Choices.SelectedItem as string);
                break;

            default:
                OkButton.Click += (_, _) => Close("ok");
                break;
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
        };
    }
}
