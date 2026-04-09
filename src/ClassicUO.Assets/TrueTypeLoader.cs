#region license

// Copyright (c) 2021, jaedan
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using ClassicUO.IO.Persistency;
using ClassicUO.Utility.Logging;
using FontStashSharp;

namespace ClassicUO.Assets;

/// <summary>
///     Contains a list of embedded fonts available for use in the application.
///     Note that this list is not exhaustive and may be expanded in the future.
/// </summary>
public static class EmbeddedFontNames
{
    public const string ROBOTO = "Roboto-Regular";
    public const string ROBOTO_BOLD = "Roboto-Bold";
    public const string ROBOTO_MONO = "Roboto-Mono";
    public const string NOTO_SANS_2_SYMBOLS = "NotoSansSymbols2-Regular";
    public const string IBM_PLEX = "ibm-plex";
    public const string ALAGARD = "alagard";
    public const string AVADONIAN = "avadonian";
    public const string KINGTHINGS_EXETER = "Kingthings Exeter";
    public const string LEAGUE_SPARTAN_BOLD = "LeagueSpartan-Bold";
    public const string UO_UNICODE = "uo-unicode-1";

    /// <summary>
    ///     The names of all embedded fonts
    /// </summary>
    public static FrozenSet<string> Names { get; }

    static EmbeddedFontNames()
    {
        // Effectively a 'const'; Ideally, this entire class would've been a string enum but alas that cannot be done.
        Names = typeof(EmbeddedFontNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(fi => fi.IsLiteral && !fi.IsInitOnly && fi.FieldType == typeof(string))
            .Select(fi => (string)fi.GetRawConstantValue())
            .ToFrozenSet();
    }
}

public class TrueTypeLoader
{
    public const string EMBEDDED_FONT = EmbeddedFontNames.ROBOTO;

    private readonly Dictionary<string, FontSystem> _fonts = new();
    /// <summary>
    /// Contains the names of all available system fonts.
    /// This can be used to render a font list without loading the fonts into memory
    /// </summary>
    private HashSet<string> _availableSystemFontFamilyNames = [];

    private TrueTypeLoader()
    {
    }

    private static TrueTypeLoader _instance;
    public static TrueTypeLoader Instance => _instance ??= new TrueTypeLoader();

    private readonly FontSystemSettings _fontSysSettings = new()
    {
        FontResolutionFactor = 2, KernelWidth = 2, KernelHeight = 2
    };

    public void Load()
    {
        LoadUserFonts();
        LoadEmbeddedFonts();
        PopulateAvailableSystemFontFamilyNames();
        BuildSysFontsCache();
    }

    /// <summary>
    ///     Loads user-provided fonts present in the 'Fonts' directory
    /// </summary>
    private void LoadUserFonts()
    {
        string fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts");

        if (!Directory.Exists(fontPath))
            Directory.CreateDirectory(fontPath);

        foreach (string ttf in Directory.GetFiles(fontPath, "*.ttf"))
        {
            var fontSystem = new FontSystem(_fontSysSettings);
            fontSystem.AddFont(File.ReadAllBytes(ttf));

            _fonts[Path.GetFileNameWithoutExtension(ttf)] = fontSystem;
        }
    }

    /// <summary>
    ///     Populates an in-memory cache of available font family names
    /// </summary>
    private void PopulateAvailableSystemFontFamilyNames() => _availableSystemFontFamilyNames =
        [..SystemFontProvider.GetSystemFonts().Select(f => f.FamilyName)];

    /// <summary>
    ///     Greedily attempts to load all available system fonts to determine which ones can be processed
    ///     by FontStashSharp and marks them accordingly in a cache file
    /// </summary>
    /// <remarks>
    ///     This is a sort-of prefetch routine; Some fonts may have valid extensions and be perfectly fine but may not be
    ///     properly loaded by <em>FontStashSharp</em>.
    ///     To allow for a consistent experience when displaying available fonts in the UI, we need to figure out, in advance,
    ///     which ones are usable and which aren't.
    ///     This method does so and stores a 'blacklist' of font families that cannot be loaded.'
    ///     It does *not* attempt to actually keep the fonts loaded in memory.
    ///     The underlying implementation currently resolves only <em>TTF, TTC</em>, and <em>OTF</em> files
    /// </remarks>
    private void BuildSysFontsCache()
    {
        int totalLoaded = 0;
        int familyCount = 0;
        bool needsCacheUpdate = false;
        var cacheDefinition = new FontPersistentDefinition();
        FontCacheData cachedData = null;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // The result of .Get can't actually be null but doesn't hurt to be defensive.
            cachedData = CacheManager.Instance.Get(cacheDefinition) ?? new FontCacheData();
            cachedData.DoNotLoadFamilies ??= [];

            if (cachedData.IsCacheFresh)
            {
                Log.Debug("Font cache is fresh, skipping rebuild");
                return;
            }

            Log.Debug("Rebuilding system fonts cache...");
            foreach (FontsByFamily fontFamily in SystemFontProvider.GetSystemFonts())
            {
                if (cachedData.DoNotLoadFamilies.Contains(fontFamily.FamilyName))
                {
                    Log.Debug($"Font family {fontFamily.FamilyName} is excluded from loading");
                    continue;
                }

                (int loadedInFamily, _) = CreateFontSystemForFamily(fontFamily);

                if (loadedInFamily <= 0)
                {
                    Log.Warn($"Font family {fontFamily.FamilyName} is empty or unavailable. It will be ignored.");
                    cachedData.DoNotLoadFamilies.Add(fontFamily.FamilyName);
                    needsCacheUpdate = true;
                }

                totalLoaded += loadedInFamily;
                ++familyCount;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load system fonts - {e.Message}");
        }

        // Update the cache if content change or timestamp has never been updated
        if (needsCacheUpdate || cachedData is { LastUpdated: null })
        {
            cachedData.LastUpdated = DateTime.UtcNow;
            if (CacheManager.Instance.Set(cacheDefinition, cachedData))
                Log.Debug("System fonts cache updated");
            else
                Log.WarnDebug("Failed to update system font cache");
        }

        stopwatch.Stop();
        Log.Debug(
            $"System fonts cache build concluded. Processed total of {totalLoaded} fonts over {familyCount} families in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    ///     Creates a FontSystem object for a given FontsByFamily object
    /// </summary>
    /// <param name="family">The family to create a <see cref="FontSystem" /> of</param>
    /// <returns>
    ///     A tuple containing the number of fonts loaded and the created <see cref="FontSystem" />
    ///     object, or (0, null) if the family is empty or could not be loaded
    /// </returns>
    private (int LoadedCount, FontSystem FontSys) CreateFontSystemForFamily(FontsByFamily family)
    {
        if (family.FontFaces.Length <= 0)
        {
            Log.Warn($"Could not find any available fonts for family '{family.FamilyName}'");
            return (0, null);
        }

        int numLoadedInSystem = 0;
        var fontSystem = new FontSystem(_fontSysSettings);
        foreach (byte[] font in family.FontFaces)
            try
            {
                fontSystem.AddFont(font);
                numLoadedInSystem++;
            }
            catch (Exception e)
            {
                Log.Warn($"Failed to load a font binary from family {family.FamilyName} - {e.Message}");
            }

        return (numLoadedInSystem, numLoadedInSystem > 0 ? fontSystem : null);
    }

    /// <summary>
    ///     Loads a font family and returns a FontSystem object if successful
    /// </summary>
    /// <param name="family">The family to load</param>
    /// <returns>
    ///     A <see cref="FontSystem" /> object created for the family or <c>null</c> of the family is empty or could not
    ///     be loaded
    /// </returns>
    private FontSystem LoadAndGetFontByFamily(FontsByFamily family)
    {
        (int loadedInFamily, FontSystem fontSystem) = CreateFontSystemForFamily(family);

        if (loadedInFamily > 0)
        {
            _fonts[family.FamilyName] = fontSystem;
            Log.Debug($"Loaded {loadedInFamily} fonts for family '{family.FamilyName}'");
        }
        else
            Log.Warn($"Could not load any fonts for family '{family.FamilyName}'. The entire family will be ignored");

        return loadedInFamily > 0 ? fontSystem : null;
    }

    /// <summary>
    ///     Loads the fonts embedded into the TUO binary
    /// </summary>
    private void LoadEmbeddedFonts()
    {
        var settings = new FontSystemSettings();

        Assembly assembly = GetType().Assembly;
        string fontAssetFolder = assembly.GetName().Name + ".fonts";
        // Get all embedded resource names
        string[] resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(fontAssetFolder))
            .ToArray();

        foreach (string resourceName in resourceNames)
        {
            Stream stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                continue;

            using (stream)
            {
                string[] rNameParts = resourceName.Split('.');
                string fName = rNameParts[^2];
#if DEBUG
                Log.Trace($"Loaded embedded font: {fName}");
#endif
                var memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);

                byte[] fileBytes = memoryStream.ToArray();

                var fontSystem = new FontSystem(settings);
                fontSystem.AddFont(fileBytes);
                _fonts[fName] = fontSystem;
            }
        }
    }

    private bool TryGetSystemFont(string name, float size, out SpriteFontBase font)
    {
        FontsByFamily? fontFamily = SystemFontProvider.GetSystemFontFamilyByName(name);
        if (fontFamily == null)
        {
            font = null;
            return false;
        }

        FontSystem fontSystem = LoadAndGetFontByFamily(fontFamily.Value);
        font = fontSystem?.GetFont(size);
        return font != null;
    }

    public SpriteFontBase GetFont(string name, float size)
    {
        // Try standard fonts first
        if (_fonts.TryGetValue(name, out FontSystem font))
            return font.GetFont(size);

        // If the font isn't present in the loaded ones but is available on the system, try to load it
        if (_availableSystemFontFamilyNames.Contains(name))
            if (TryGetSystemFont(name, size, out SpriteFontBase sysFont))
                return sysFont;

        // Use the default embedded font as a fallback
        if (_fonts.TryGetValue(EmbeddedFontNames.ROBOTO, out FontSystem embeddedFont))
            return embeddedFont.GetFont(size);

        // Otherwise, use the first font we have or give up with a null.
        return _fonts.Count > 0 ? _fonts.First().Value.GetFont(size) : null;
    }

    public SpriteFontBase GetFont(string name) => GetFont(name, 12);

    public string[] Fonts => _fonts.Keys.Concat(_availableSystemFontFamilyNames).ToArray();
}

internal class FontCacheData
{
    public DateTime? LastUpdated { get; set; }
    public List<string> DoNotLoadFamilies { get; set; } = [];

    [JsonIgnore]
    // We can re-build the cache every 30 days for good measure
    public bool IsCacheFresh => LastUpdated != null && DateTime.UtcNow - LastUpdated.Value < TimeSpan.FromDays(30);
}

internal class FontPersistentDefinition : PersistentItemDefinition<CacheType, FontCacheData>
{
    public override CacheType Key => CacheType.Font;
}
