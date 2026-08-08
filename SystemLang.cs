using System.Globalization;

// ═══════════════════════════════════════════════════════════════════════
//  SystemLang — machine language resolution (two-letter ISO code)
//
//  INVARIANT (project rule): application code MUST NOT contain settings for a
//  specific language, keyboard layout, regional formats, etc. — execution adapts
//  to ANY settings of the computer it is running on. Every component that needs
//  a language asks for it here (SystemLang.Get()), never with a hardcoded default
//  in the code.
//
//  Used to pick TTS voices and the speech-recognition language.
//  Respects the machine's settings (CurrentUICulture: Windows regional
//  settings / Linux locale). A headless/invariant server (culture "iv", e.g.
//  LANG=C/POSIX) falls back to "en" — the sensible default when no language
//  is configured.
// ═══════════════════════════════════════════════════════════════════════
/// <summary>
/// Resolves the machine's language (two-letter ISO code, CurrentUICulture) for TTS
/// voices and speech recognition. Never hardcodes a language — see the INVARIANT
/// header above. Falls back to "en" for invariant/headless servers.
/// </summary>
public static class SystemLang
{
    /// <summary>Two-letter ISO language of the machine (CurrentUICulture), or "en" for invariant.</summary>
    public static string Get()
    {
        try
        {
            var l = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (!string.IsNullOrWhiteSpace(l) && !l.Equals("iv", StringComparison.OrdinalIgnoreCase))
                return l;
        }
        catch { }
        return "en";
    }
}
