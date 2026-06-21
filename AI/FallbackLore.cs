/// <summary>
/// Generic fallback lore used when Ollama is unavailable or returns invalid JSON.
/// Always produces coherent narrative derived purely from the theme word — no
/// hardcoded theme presets.
/// </summary>
public static class FallbackLore
{
    public static LoreData For(string theme, int totalLevels = 5)
    {
        string t = string.IsNullOrWhiteSpace(theme) ? "mystery" : theme.Trim().ToLowerInvariant();
        string T = char.ToUpper(t[0]) + t.Substring(1); // Capitalised

        return new LoreData
        {
            title = $"The {T} Chronicles",
            intro = $"The realm has been warped by raw {t} power, and once-familiar paths now hide traps, corrupted guardians, and living storms. " +
                    $"Only one runner remains bold enough to cross the broken frontier and challenge what rules at its core.",
            goal  = $"Survive all {totalLevels} sectors, recover the lost seals of balance, and defeat the {T} Warden before the corruption becomes permanent.",
            levelFlavors = new[]
            {
                $"Sector I - Borderlands: scouting beasts and unstable terrain test your rhythm while the first seal lies hidden among ruined outposts.",
                $"Sector II - Fracture Field: the {t} current thickens, patrols coordinate their attacks, and every platform feels less forgiving.",
                $"Sector III - Inner Veins: this is the engine of the corruption, where airborne predators and ranged sentries guard the second seal.",
                $"Sector IV - Citadel Approach: the sky darkens, alarms echo through the fortress rim, and the final ascent leaves no room for mistakes.",
                $"Sector V - Throne Core: the chamber opens, the seals resonate, and the {T} Warden emerges for the last battle."
            },
            bossName = $"The {T} Warden",
            bossDesc = $"A colossal guardian fused with concentrated {t} energy, shifting between relentless charges and punishing counterattacks."
        };
    }
}
