using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

using spkl.Diffs;

namespace DumpJson;

public class DumpJsonServerSystem : ModSystem {
  private ICoreServerAPI _api;
  private Dictionary<string, string> _prePatchAssets;

  public override void Start(ICoreAPI api) {
    base.Start(api);
    
    // Only capture on server side
    if (api.Side != EnumAppSide.Server) {
      return;
    }
    
    _api = (ICoreServerAPI)api;
    _prePatchAssets = new Dictionary<string, string>();
    
    _api.Logger.Notification("dump json - capturing pre-patch assets at Start phase");
    
    var allAssets = _api.Assets.GetMany("");
    if (allAssets != null) {
      _api.Logger.Notification("dump json - found {0} assets at Start", allAssets.Count);
      
      foreach (var asset in allAssets.Where(asset => asset != null)) {
        try {
          string assetPath = asset.Location.Path;
          if (assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            string modDomain = asset.Location.Domain ?? "unknown";
            string pathWithModDomain = Path.Combine(modDomain, assetPath);
            // Parse and reformat JSON to ensure consistent formatting
            try {
              JToken parsed = JToken.Parse(asset.ToText());
              string formatted = parsed.ToString(Formatting.Indented);
              _prePatchAssets[pathWithModDomain] = formatted;
            } catch {
              // If parsing fails, store as-is
              _prePatchAssets[pathWithModDomain] = asset.ToText();
            }
          }
        } catch (Exception ex) {
          _api.Logger.Debug("dump json - failed to capture pre-patch asset {0}: {1}",
            asset.Location.Path, ex.Message);
        }
      }
    }
    
    _api.Logger.Notification("dump json - captured {0} pre-patch assets", _prePatchAssets.Count);
  }

  public override void StartServerSide(ICoreServerAPI api) {
    base.StartServerSide(api);
    _api = api;
    
    // Only initialize if not already done in Start
    if (_prePatchAssets == null) {
      _prePatchAssets = new Dictionary<string, string>();
    }
    
    // Dump assets after patches are applied
    api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, OnServerReady);
  }

  private void OnServerReady() {
    _api.Logger.Notification("dump json server system ready");

    string dumpPath = Path.Combine(GamePaths.Logs, "dump", "server");
    DirectoryInfo dumpDir = new(dumpPath);

    try {
      if (dumpDir.Exists) {
        dumpDir.Delete(true);
      }
    } catch (DirectoryNotFoundException) {
    }

    dumpDir.Create();
    _api.Logger.Notification("dump json - created dump directory at {0}", dumpPath);

    JsonSerializer serializer =
      new() {
        Formatting = Formatting.Indented,
        ContractResolver = new DuplicateFieldResolvingContractResolver {
          NamingStrategy =
            new CamelCaseNamingStrategy {
              OverrideSpecifiedNames =
                true
            }
        }
      };

    _api.Logger.Notification("dump json - beginning asset dump");

    // Dump post-patch assets
    DumpPostPatchAssets(dumpPath, serializer);
    
    // Dump pre-patch assets
    DumpPrePatchAssets(dumpPath, serializer);
    
    // Dump diffs
    DumpAssetDiffs(dumpPath, serializer);

    _api.Logger.Notification("dump json - asset dump complete");
  }

  private void DumpPrePatchAssets(string dumpPath, JsonSerializer serializer) {
    var watch = Stopwatch.StartNew();

    string prePatchPath = Path.Combine(dumpPath, "pre-patch");
    if (!Directory.Exists(prePatchPath)) {
      Directory.CreateDirectory(prePatchPath);
    }

    int assetCount = 0;

    foreach (var kvp in _prePatchAssets) {
      try {
        string pathWithModDomain = kvp.Key;
        
        string filePath = CreateSafePath(prePatchPath, pathWithModDomain);

        using StreamWriter file = File.CreateText(filePath);
        file.Write(kvp.Value);
        assetCount++;
      } catch (Exception ex) {
        _api.Logger.Debug("dump json - failed to dump pre-patch asset: {0}",
          ex.Message);
      }
    }

    watch.Stop();
    _api.Logger.Notification("dump json - dumped {0} pre-patch assets in {1}",
      assetCount, watch.Elapsed);
  }

  private void DumpPostPatchAssets(string dumpPath, JsonSerializer serializer) {
    var watch = Stopwatch.StartNew();

    string postPatchPath = Path.Combine(dumpPath, "post-patch");
    if (!Directory.Exists(postPatchPath)) {
      Directory.CreateDirectory(postPatchPath);
    }

    var allAssets = _api.Assets.GetMany("");
    if (allAssets == null || allAssets.Count == 0) {
      _api.Logger.Notification("dump json - no post-patch assets found");
      watch.Stop();
      return;
    }

    int assetCount = 0;
    int skippedCount = 0;

    foreach (var asset in allAssets.Where(asset => asset != null)) {
      try {
        string assetPath = asset.Location.Path;
        
        if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
          //_api.Logger.Debug("dump json - skipped binary asset {0}", assetPath);
          skippedCount++;
          continue;
        }
        
        string modDomain = asset.Location.Domain ?? "unknown";
        string pathWithModDomain = Path.Combine(modDomain, assetPath);
        
        string filePath = CreateSafePath(postPatchPath, pathWithModDomain);

        using StreamWriter file = File.CreateText(filePath);
        // Parse and reformat for consistent output
        try {
          JToken parsed = JToken.Parse(asset.ToText());
          string formatted = parsed.ToString(Formatting.Indented);
          file.Write(formatted);
        } catch {
          // If parsing fails, write as-is
          file.Write(asset.ToText());
        }
        assetCount++;
      } catch (Exception ex) {
        _api.Logger.Debug("dump json - failed to dump post-patch asset: {0}",
          ex.Message);
      }
    }

    watch.Stop();
    _api.Logger.Notification("dump json - dumped {0} post-patch assets ({1} skipped) in {2}",
      assetCount, skippedCount, watch.Elapsed);
  }

  private void DumpAssetDiffs(string dumpPath, JsonSerializer serializer) {
    var watch = Stopwatch.StartNew();

    string diffPath = Path.Combine(dumpPath, "diffs");
    if (!Directory.Exists(diffPath)) {
      Directory.CreateDirectory(diffPath);
    }

    var allAssets = _api.Assets.GetMany("");
    if (allAssets == null) {
      _api.Logger.Notification("dump json - no post-patch assets found for diffing");
      watch.Stop();
      return;
    }

    int diffCount = 0;
    int unchangedCount = 0;
    int newAssetCount = 0;
    int deletedAssetCount = 0;

    // Track which pre-patch assets have been processed
    var processedPrePatchAssets = new HashSet<string>();

    // Process all post-patch assets
    foreach (var asset in allAssets.Where(asset => asset != null)) {
      try {
        string assetPath = asset.Location.Path;

        
        if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
          continue;
        }
        
        string modDomain = asset.Location.Domain ?? "unknown";
        string pathWithModDomain = Path.Combine(modDomain, assetPath);
        string postPatchJson = asset.ToText();
        
        // Check if asset existed pre-patch
        if (!_prePatchAssets.TryGetValue(pathWithModDomain, out string prePatchJson)) {
          // New asset added by patch
          newAssetCount++;
          
          string diffPathWithModDomain = Path.Combine("new", modDomain, assetPath);
          string diffFilePath = CreateSafeDiffPath(diffPath, diffPathWithModDomain);

          WriteDiffFile(diffFilePath, "", pathWithModDomain, postPatchJson, pathWithModDomain, _api);
          diffCount++;
          continue;
        }

        processedPrePatchAssets.Add(pathWithModDomain);

        // Compare pre and post patch
        JToken prePatch = JToken.Parse(prePatchJson);
        JToken postPatch = JToken.Parse(postPatchJson);
        
        if (!JToken.DeepEquals(prePatch, postPatch)) {
          // Asset was modified by patch
          string diffPathWithModDomain = Path.Combine(modDomain, assetPath);
          string diffFilePath = CreateSafeDiffPath(diffPath, diffPathWithModDomain);

          // Format post-patch for consistent diffing
          string formattedPostPatch = postPatch.ToString(Formatting.Indented);
          WriteDiffFile(diffFilePath, prePatchJson, pathWithModDomain, formattedPostPatch, pathWithModDomain, _api);
          diffCount++;
        } else {
          unchangedCount++;
        }
      } catch (Exception ex) {
        _api.Logger.Debug("dump json - failed to diff asset: {0}",
          ex.Message);
      }
    }

    // Process pre-patch assets that no longer exist in post-patch (deleted assets)
    foreach (var kvp in _prePatchAssets) {
      string pathWithModDomain = kvp.Key;
      
      if (processedPrePatchAssets.Contains(pathWithModDomain)) {
        continue;
      }

      try {
        deletedAssetCount++;
        
        string diffPathWithModDomain = Path.Combine("deleted", pathWithModDomain);
        string diffFilePath = CreateSafeDiffPath(diffPath, diffPathWithModDomain);

        WriteDiffFile(diffFilePath, kvp.Value, pathWithModDomain, "", pathWithModDomain, _api);
        diffCount++;
      } catch (Exception ex) {
        _api.Logger.Debug("dump json - failed to diff deleted asset {0}: {1}",
          pathWithModDomain, ex.Message);
      }
    }

    watch.Stop();
    _api.Logger.Notification("dump json - dumped {0} diffs ({1} unchanged, {2} new, {3} deleted) in {4}",
      diffCount, unchangedCount, newAssetCount, deletedAssetCount, watch.Elapsed);
  }

  private static string CreateSafePath(string folder, string code) {
    code = code.Replace(':', '-');
    code = "./" + code;
    if (Path.IsPathRooted(code)) {
      throw new ArgumentException(
        $"Found a code that cannot be safely turned into a path: {code}");
    }

    FileInfo file =
      new(Path.Combine(folder, code));
    DirectoryInfo folderInfo = new(folder);
    DirectoryInfo parent = file.Directory;
    // Verify the combined path uses folder as an ancestor.
    while (string.Compare(parent!.FullName, folderInfo.FullName,
             StringComparison.OrdinalIgnoreCase) != 0) {
      parent = parent.Parent ?? throw new ArgumentException(
        $"Found a code that cannot be safely turned into a path: {code}");
    }

    if (!file.Directory!.Exists) {
      file.Directory.Create();
    }

    return file.FullName;
  }

  private static string CreateSafeDiffPath(string folder, string code) {
    // Append .diff to the filename, keeping the .json extension
    code = code + ".diff";
    code = code.Replace(':', '-');
    code = "./" + code;
    if (Path.IsPathRooted(code)) {
      throw new ArgumentException(
        $"Found a code that cannot be safely turned into a path: {code}");
    }

    FileInfo file = new(Path.Combine(folder, code));
    DirectoryInfo folderInfo = new(folder);
    DirectoryInfo parent = file.Directory;
    // Verify the combined path uses folder as an ancestor.
    while (string.Compare(parent!.FullName, folderInfo.FullName,
             StringComparison.OrdinalIgnoreCase) != 0) {
      parent = parent.Parent ?? throw new ArgumentException(
        $"Found a code that cannot be safely turned into a path: {code}");
    }

    if (!file.Directory!.Exists) {
      file.Directory.Create();
    }

    return file.FullName;
  }

  private static void WriteDiffFile(string filePath, string prePatchJson, string prePatchAssetPath, string postPatchJson, string postPatchAssetPath, ICoreServerAPI api) {
    try {
      using StreamWriter file = File.CreateText(filePath);
      
      if (string.IsNullOrEmpty(prePatchJson)) {
        // New file - just write the post-patch content
        file.WriteLine("New file added by patch:");
        file.WriteLine();
        file.Write(postPatchJson);
      } else if (string.IsNullOrEmpty(postPatchJson)) {
        // Deleted file
        file.WriteLine("File deleted by patch:");
        file.WriteLine();
        file.Write(prePatchJson);
      } else {
        // Both pre and post are already formatted consistently, so we can diff directly
        var prePatchLines = prePatchJson.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var postPatchLines = postPatchJson.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        string prePatchPath = Path.Combine("dump", "server", "pre-patch", prePatchAssetPath).Replace("\\", "/");
        string postPatchPath = Path.Combine("dump", "server", "post-patch", postPatchAssetPath).Replace("\\", "/");
        
        file.WriteLine($"--- {prePatchPath}");
        file.WriteLine($"+++ {postPatchPath}");
        
        // Generate unified diff with proper line offsets
        var diffHunks = GenerateDiffHunks(prePatchLines, postPatchLines, contextLines: 3);
        
        foreach (var hunk in diffHunks) {
          file.WriteLine($"@@ -{hunk.PreStartLine},{hunk.PreLineCount} +{hunk.PostStartLine},{hunk.PostLineCount} @@");
          foreach (var line in hunk.Lines) {
            file.WriteLine(line);
          }
        }
      }
    } catch (Exception ex) {
      api.Logger.Debug("dump json - failed to write diff file {0}: {1}", filePath, ex.Message);
    }
  }

  private class DiffHunk {
    public int PreStartLine { get; set; }
    public int PreLineCount { get; set; }
    public int PostStartLine { get; set; }
    public int PostLineCount { get; set; }
    public List<string> Lines { get; set; } = new();
  }

  private static string[] TrimTrailingEmptyLines(string[] lines) {
    int lastNonEmpty = lines.Length - 1;
    while (lastNonEmpty >= 0 && string.IsNullOrEmpty(lines[lastNonEmpty])) {
      lastNonEmpty--;
    }
    if (lastNonEmpty < lines.Length - 1) {
      Array.Resize(ref lines, lastNonEmpty + 1);
    }
    return lines;
  }

  private static List<DiffHunk> GenerateDiffHunks(string[] prePatchLines, string[] postPatchLines, int contextLines) {
    var hunks = new List<DiffHunk>();
    
    // Remove empty trailing lines from split
    prePatchLines = TrimTrailingEmptyLines(prePatchLines);
    postPatchLines = TrimTrailingEmptyLines(postPatchLines);
    
    // Use Myers' diff algorithm for optimal diffs
    var myersDiff = new MyersDiff<string>(prePatchLines, postPatchLines);
    var editScript = myersDiff.GetEditScript().ToList();
    
    // Process each edit operation as a separate hunk
    foreach (var (lineA, lineB, countA, countB) in editScript) {
      if (countA == 0 && countB == 0) {
        // No changes in this edit
        continue;
      }
      
      // Calculate hunk boundaries with context
      int hunkStartPre = Math.Max(0, lineA - contextLines);
      int hunkStartPost = Math.Max(0, lineB - contextLines);
      int hunkEndPre = Math.Min(prePatchLines.Length, lineA + countA + contextLines);
      int hunkEndPost = Math.Min(postPatchLines.Length, lineB + countB + contextLines);
      
      var hunkLines = new List<string>();
      
      // Add leading context (matched lines before the change)
      for (int i = hunkStartPre; i < lineA && i < prePatchLines.Length; i++) {
        hunkLines.Add(" " + prePatchLines[i]);
      }
      
      // Add removed lines
      for (int i = 0; i < countA && lineA + i < prePatchLines.Length; i++) {
        hunkLines.Add("-" + prePatchLines[lineA + i]);
      }
      
      // Add inserted lines
      for (int i = 0; i < countB && lineB + i < postPatchLines.Length; i++) {
        hunkLines.Add("+" + postPatchLines[lineB + i]);
      }
      
      // Add trailing context (matched lines after the change)
      int preTrailingStart = lineA + countA;
      int postTrailingStart = lineB + countB;
      int trailingLines = 0;
      
      while (trailingLines < contextLines && 
             preTrailingStart + trailingLines < hunkEndPre && 
             postTrailingStart + trailingLines < hunkEndPost) {
        hunkLines.Add(" " + prePatchLines[preTrailingStart + trailingLines]);
        trailingLines++;
      }
      
      // Create the hunk
      var hunk = new DiffHunk();
      hunk.PreStartLine = hunkStartPre + 1;
      hunk.PostStartLine = hunkStartPost + 1;
      hunk.PreLineCount = hunkEndPre - hunkStartPre;
      hunk.PostLineCount = hunkEndPost - hunkStartPost;
      hunk.Lines = hunkLines;
      
      hunks.Add(hunk);
    }
    
    return hunks;
  }
}
