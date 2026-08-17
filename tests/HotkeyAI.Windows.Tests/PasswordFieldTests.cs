using HotkeyAI.Windows;

namespace HotkeyAI.Windows.Tests;

/// <summary>
/// Which focused controls the password-style check is willing to read a style bit from.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding M6. PLAN.md control 3 claimed input would be refused when
/// "a control with the password style" has focus; the code checked two window class names and the
/// integrity level, so a credential *dialog* was caught while a password *field* inside an ordinary
/// window was not. The check now asks the foreground thread which control has the focus and reads
/// its <c>ES_PASSWORD</c> style.
/// <para>
/// The class test has to come first, because <c>0x0020</c> means something else entirely on a button
/// or a list box — read blindly it would invent password fields everywhere. And it has to understand
/// superclassing: a live probe against a WinForms <c>TextBox</c> with <c>UseSystemPasswordChar</c>
/// reported its class as <c>WindowsForms10.EDIT.app.0.1405e41_r25_ad1</c>, so the exact-match list
/// this started as found a plain Win32 dialog's password box and missed every managed one — which is
/// most of them. That is the bug these tests exist for.
/// </para>
/// </remarks>
public sealed class PasswordFieldTests
{
    [Theory]
    [InlineData("Edit")]                                    // Win32 dialogs, including LogonUI
    [InlineData("EDIT")]                                    // the class name is not case-sensitive
    [InlineData("RichEdit20W")]
    [InlineData("RICHEDIT50W")]
    public void APlainEditControlIsRecognised(string className)
    {
        Assert.True(WindowsInput.IsEditControl(className));
    }

    [Theory]
    [InlineData("WindowsForms10.EDIT.app.0.1405e41_r25_ad1")]   // observed on this machine
    [InlineData("WindowsForms10.EDIT.app.0.2c908d5_r6_ad1")]    // the hash differs per app domain
    [InlineData("WindowsForms10.RICHEDIT50W.app.0.1405e41")]
    public void ASuperclassedEditControlIsRecognised(string className)
    {
        Assert.True(WindowsInput.IsEditControl(className));
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("ComboBox")]
    [InlineData("ListBox")]
    [InlineData("Static")]
    [InlineData("WindowsForms10.BUTTON.app.0.1405e41_r25_ad1")]
    [InlineData("Chrome_RenderWidgetHostHWND")]
    [InlineData("")]
    public void AnythingElseIsNot(string className)
    {
        // The false-positive direction is the one that breaks working automations: a button whose
        // 0x0020 bit happens to be set would otherwise refuse every send_keys aimed at it.
        Assert.False(WindowsInput.IsEditControl(className));
    }

    [Theory]
    [InlineData("WindowsForms10")]
    [InlineData("WindowsForms10.")]
    [InlineData("WindowsFormsEDIT")]
    public void AMalformedSuperclassNameIsNotAnEditControl(string className)
    {
        // The split has to survive names with no second segment rather than throwing on the input
        // path, where an exception would abort an automation mid-run.
        Assert.False(WindowsInput.IsEditControl(className));
    }
}
