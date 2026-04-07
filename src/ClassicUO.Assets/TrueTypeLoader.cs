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
    /// The names of all embedded fonts
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
        string fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts");

        if (!Directory.Exists(fontPath))
            Directory.CreateDirectory(fontPath);

        foreach (string ttf in Directory.GetFiles(fontPath, "*.ttf"))
        {
            var fontSystem = new FontSystem(_fontSysSettings);
            fontSystem.AddFont(File.ReadAllBytes(ttf));

            _fonts[Path.GetFileNameWithoutExtension(ttf)] = fontSystem;
        }

        LoadEmbeddedFonts();
        LoadSystemFonts();
    }

    /// <summary>
    /// Loads fonts available on the local system
    /// </summary>
    /// <remarks>
    /// The underlying implementation currently resolves only <em>TTF, TTC</em>, and <em>OTF</em> files
    /// </remarks>
    private void LoadSystemFonts()
    {
        int totalLoaded = 0;
        int familyCount = 0;
        bool needsCacheUpdate = false;
        var cacheDefinition = new FontPersistentDefinition();
        FontCacheData cachedData = null;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            cachedData = CacheManager.Instance.Get(cacheDefinition);
            foreach (FontsByFamily fontFamily in SystemFontProvider.GetSystemFonts())
            {
                if (cachedData?.DoNotLoadFamilies?.Contains(fontFamily.FamilyName) == true)
                {
                    Log.Debug($"Font family {fontFamily.FamilyName} is excluded from loading");
                    continue;
                }

                int loadedInFamily = LoadFontFamily(cachedData, fontFamily);
                totalLoaded += loadedInFamily;
                ++familyCount;

                // If nothing was loaded for the family, we'll need to update the cache to ignore it in the future.
                if (loadedInFamily <= 0)
                    needsCacheUpdate = true;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load system fonts - {e.Message}");
        }

        if (needsCacheUpdate && cachedData != null)
        {
            if (!CacheManager.Instance.Set(cacheDefinition, cachedData))
                Log.WarnDebug($"Failed to update system font cache");
        }

        stopwatch.Stop();
        Log.Debug($"Loaded a total of {totalLoaded} system fonts over {familyCount} families in {stopwatch.ElapsedMilliseconds}ms");
    }

    private int LoadFontFamily(FontCacheData cacheData, FontsByFamily family)
    {
        if (_fonts.ContainsKey(family.FamilyName))
        {
            Log.WarnDebug($"System font family {family.FamilyName} appears more than once");
            return 0;
        }

        if (family.FontFaces.Length <= 0)
        {
            Log.Warn($"Could not find any available fonts for family '{family.FamilyName}'");
            cacheData.DoNotLoadFamilies.Add(family.FamilyName);
            return 0;
        }

        int numLoadedInSystem = 0;
        var fontSystem = new FontSystem(_fontSysSettings);
        foreach (byte[] font in family.FontFaces)
        {
            try
            {
                fontSystem.AddFont(font);
                numLoadedInSystem++;
            }
            catch (Exception e)
            {
                Log.Warn($"Failed to load a font binary from family {family.FamilyName} - {e.Message}");
            }
        }

        if (numLoadedInSystem > 0)
        {
            _fonts[family.FamilyName] = fontSystem;
            Log.Debug($"Loaded {numLoadedInSystem} fonts for family '{family.FamilyName}'");
        }
        else
        {
            cacheData.DoNotLoadFamilies.Add(family.FamilyName);
            Log.Warn($"Could not load any fonts for family '{family.FamilyName}'. The entire family will be ignored");
        }

        return numLoadedInSystem;
    }

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

    public SpriteFontBase GetFont(string name, float size)
    {
        if (_fonts.TryGetValue(name, out FontSystem font))
            return font.GetFont(size);

        // Use the default embedded font as a fallback
        if (_fonts.TryGetValue(EmbeddedFontNames.ROBOTO, out FontSystem embeddedFont))
            return embeddedFont.GetFont(size);

        // Otherwise, use the first font we have or give up with a null.
        return _fonts.Count > 0 ? _fonts.First().Value.GetFont(size) : null;
    }

    public SpriteFontBase GetFont(string name) => GetFont(name, 12);

    public string[] Fonts => _fonts.Keys.ToArray();
}

internal class FontCacheData
{
    public List<string> DoNotLoadFamilies { get; set; } = [];
}

internal class FontPersistentDefinition : PersistentItemDefinition<CacheType, FontCacheData>
{
    public override CacheType Key => CacheType.Font;
}
