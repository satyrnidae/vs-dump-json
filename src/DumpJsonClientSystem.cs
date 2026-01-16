using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace DumpJson;

public class DumpJsonClientSystem : ModSystem {
  private ICoreAPI _api;

  public override void AssetsLoaded(ICoreAPI api) {
    base.AssetsLoaded(api);
    // This is roughly the closest method that is called by the mod system on
    // the client side, in both single player and multiplayer, before the server
    // assets are freed. However, at this point only the client side assets have
    // been loaded. The server side assets have not been received yet. There are
    // no events that this mod can be directly hooked into to intercept when the
    // server assets are received but not freed yet. So instead intercept the
    // log message. An alternative would be use a Harmony patch, but Harmony
    // patches do not currently work on ARM computers.
    api.Logger.Notification("dump json assets loaded");
    _api = api;
    api.Logger.EntryAdded += OnLogMessage;
  }

  private void OnLogMessage(EnumLogType logType, string message,
    object[] args) {
    if (message != "Server assets loaded") {
      return;
    }

    // Now that the message has been intercepted, stop intercepting additional
    // messages, both for performance reasons, and to prevent this method from
    // getting called a second time.
    _api.Logger.EntryAdded -= OnLogMessage;
    _api.Logger.Notification(
      "dump json found the 'Server assets loaded' message.");

    ClientSystemStartup startup = ClientSystemStartup.instance;

    FieldInfo srvAssetsPacketField =
      typeof(ClientSystemStartup)
        .GetField("pkt_srvrassets",
          BindingFlags.Instance | BindingFlags.NonPublic);
    Packet_ServerAssets srvAssetsPacket =
      (Packet_ServerAssets)srvAssetsPacketField!.GetValue(startup);

    string dumpPath = Path.Combine(GamePaths.Logs, "dump", "client");
    DirectoryInfo dumpDir = new(dumpPath);
    try {
      // Clear out any data from previous servers.
      dumpDir.Delete(true);
    } catch (DirectoryNotFoundException) {
    }

    dumpDir.Create();

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

    string blocksPath = Path.Combine(dumpPath, "blocks");
    if (!Directory.Exists(blocksPath)) {
      Directory.CreateDirectory(blocksPath);
    }

    DumpBlocks(srvAssetsPacket!.Blocks, srvAssetsPacket.BlocksCount, serializer,
      blocksPath);

    string itemsPath = Path.Combine(dumpPath, "items");
    if (!Directory.Exists(itemsPath)) {
      Directory.CreateDirectory(itemsPath);
    }

    DumpItems(srvAssetsPacket.Items, srvAssetsPacket.ItemsCount, serializer,
      itemsPath);

    string entitiesPath = Path.Combine(dumpPath, "entities");
    if (!Directory.Exists(entitiesPath)) {
      Directory.CreateDirectory(entitiesPath);
    }

    DumpEntities(srvAssetsPacket.Entities, srvAssetsPacket.EntitiesCount,
      serializer, entitiesPath);

    string recipesPath = Path.Combine(dumpPath, "recipes");
    if (!Directory.Exists(recipesPath)) {
      Directory.CreateDirectory(recipesPath);
    }

    DumpRecipes(srvAssetsPacket.Recipes, srvAssetsPacket.RecipesCount, serializer,
      recipesPath);
  }

  private void DumpBlocks(Packet_BlockType[] blocks, int blocksCount,
    JsonSerializer serializer, string blocksPath) {
    var watch = Stopwatch.StartNew();

    for (int i = 0; i < blocksCount; ++i) {
      Packet_BlockType block = blocks[i];
      using StreamWriter file =
        File.CreateText(CreateSafePath(blocksPath, block.Code));
      serializer.Serialize(file, block);
    }

    watch.Stop();
    _api.Logger.Notification("dump json - dumped {0} blocks in {1}",
      blocksCount, watch.Elapsed);
  }

  private static string CreateSafePath(string folder, string code) {
    code = code.Replace('.', '-');
    code = code.Replace(':', '/');
    code = "./" + code;
    if (Path.IsPathRooted(code)) {
      throw new ArgumentException(
        $"Found a code that cannot be safely turned into a path: {code}");
    }

    FileInfo file =
      new(Path.Combine(folder, Path.ChangeExtension(code, ".json")));
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

  private void DumpItems(Packet_ItemType[] items, int itemsCount,
    JsonSerializer serializer, string itemsPath) {
    var watch = Stopwatch.StartNew();

    for (int i = 0; i < itemsCount; ++i) {
      Packet_ItemType item = items[i];
      using StreamWriter file =
        File.CreateText(CreateSafePath(itemsPath, item.Code));
      serializer.Serialize(file, item);
    }

    watch.Stop();
    _api.Logger.Notification("dump json - dumped {0} items in {1}", itemsCount,
      watch.Elapsed);
  }

  private void DumpEntities(Packet_EntityType[] entities, int entitiesCount,
    JsonSerializer serializer, string entitiesPath) {
    var watch = Stopwatch.StartNew();

    for (int i = 0; i < entitiesCount; ++i) {
      Packet_EntityType entity = entities[i];
      using StreamWriter file =
        File.CreateText(CreateSafePath(entitiesPath, entity.Code));
      serializer.Serialize(file, entity);
    }

    watch.Stop();
    _api.Logger.Notification("dump json - dumped {0} entities in {1}",
      entitiesCount, watch.Elapsed);
  }

  private void DumpRecipes(Packet_Recipes[] recipes, int recipesCount,
    JsonSerializer serializer, string recipesPath) {
    var watch = Stopwatch.StartNew();

    for (int i = 0; i < recipesCount; ++i) {
      Packet_Recipes recipe = recipes[i];
      using StreamWriter file =
        File.CreateText(CreateSafePath(recipesPath, recipe.Code));
      serializer.Serialize(file, recipe);
    }

    watch.Stop();
    _api.Logger.Notification("dump json - dumped {0} recipes in {1}",
      recipesCount, watch.Elapsed);
  }
}
