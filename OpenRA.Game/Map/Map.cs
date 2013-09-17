#region Copyright & License Information
/*
 * Copyright 2007-2014 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OpenRA.FileSystem;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA
{
	public class MapOptions
	{
		public bool? Cheats;
		public bool? Crates;
		public bool? Fog;
		public bool? Shroud;
		public bool? AllyBuildRadius;
		public bool? FragileAlliances;
		public int? StartingCash;
		public string TechLevel;
		public bool ConfigurableStartingUnits = true;
		public string[] Difficulties = { };

		public void UpdateServerSettings(Session.Global settings)
		{
			if (Cheats.HasValue)
				settings.AllowCheats = Cheats.Value;
			if (Crates.HasValue)
				settings.Crates = Crates.Value;
			if (Fog.HasValue)
				settings.Fog = Fog.Value;
			if (Shroud.HasValue)
				settings.Shroud = Shroud.Value;
			if (AllyBuildRadius.HasValue)
				settings.AllyBuildRadius = AllyBuildRadius.Value;
			if (StartingCash.HasValue)
				settings.StartingCash = StartingCash.Value;
			if (FragileAlliances.HasValue)
				settings.FragileAlliances = FragileAlliances.Value;
		}
	}

	public class Map
	{
		[FieldLoader.Ignore] public IFolder Container;
		public string Path { get; private set; }

		// Yaml map data
		public string Uid { get; private set; }
		public int MapFormat;
		public bool Selectable = true;
		public bool UseAsShellmap;
		public string RequiresMod;

		public string Title;
		public string Type = "Conquest";
		public string Description;
		public string Author;
		public string Tileset;
		public bool AllowStartUnitConfig = true;
		public Bitmap CustomPreview;

		[FieldLoader.LoadUsing("LoadOptions")]
		public MapOptions Options;

		static object LoadOptions(MiniYaml y)
		{
			var options = new MapOptions();
			var nodesDict = y.ToDictionary();
			if (nodesDict.ContainsKey("Options"))
				FieldLoader.Load(options, nodesDict["Options"]);

			return options;
		}

		[FieldLoader.Ignore] public Lazy<Dictionary<string, ActorReference>> Actors;

		public int PlayerCount { get { return Players.Count(p => p.Value.Playable); } }

		public Rectangle Bounds;

		// Yaml map data
		[FieldLoader.Ignore] public Dictionary<string, PlayerReference> Players = new Dictionary<string, PlayerReference>();
		[FieldLoader.Ignore] public Lazy<List<SmudgeReference>> Smudges;

		[FieldLoader.Ignore] public List<MiniYamlNode> RuleDefinitions = new List<MiniYamlNode>();
		[FieldLoader.Ignore] public List<MiniYamlNode> SequenceDefinitions = new List<MiniYamlNode>();
		[FieldLoader.Ignore] public List<MiniYamlNode> VoxelSequenceDefinitions = new List<MiniYamlNode>();
		[FieldLoader.Ignore] public List<MiniYamlNode> WeaponDefinitions = new List<MiniYamlNode>();
		[FieldLoader.Ignore] public List<MiniYamlNode> VoiceDefinitions = new List<MiniYamlNode>();
		[FieldLoader.Ignore] public List<MiniYamlNode> NotificationDefinitions = new List<MiniYamlNode>();
		[FieldLoader.Ignore] public List<MiniYamlNode> TranslationDefinitions = new List<MiniYamlNode>();

		// Binary map data
		[FieldLoader.Ignore] public byte TileFormat = 1;
		public int2 MapSize;

		[FieldLoader.Ignore] public Lazy<CellLayer<TerrainTile>> MapTiles;
		[FieldLoader.Ignore] public Lazy<CellLayer<ResourceTile>> MapResources;
		[FieldLoader.Ignore] public CellLayer<int> CustomTerrain;

		[FieldLoader.Ignore] Lazy<Ruleset> rules;
		public Ruleset Rules { get { return rules != null ? rules.Value : null; } }
		public SequenceProvider SequenceProvider { get { return Rules.Sequences[Tileset]; } }

		[FieldLoader.Ignore] public CellRegion Cells;

		public static Map FromTileset(TileSet tileset)
		{
			var size = new Size(1, 1);
			var tileRef = new TerrainTile(tileset.Templates.First().Key, (byte)0);

			var makeMapTiles =  Exts.Lazy(() =>
			{
				var ret = new CellLayer<TerrainTile>(size);
				ret.Clear(tileRef);
				return ret;
			});

			var map = new Map()
			{
				Title = "Name your map here",
				Description = "Describe your map here",
				Author = "Your name here",
				MapSize = new int2(size),
				Tileset = tileset.Id,
				Options = new MapOptions(),
				MapResources = Exts.Lazy(() => new CellLayer<ResourceTile>(size)),
				MapTiles = makeMapTiles,
				Actors = Exts.Lazy(() => new Dictionary<string, ActorReference>()),
				Smudges = Exts.Lazy(() => new List<SmudgeReference>())
			};
			map.PostInit();

			return map;
		}

		void AssertExists(string filename)
		{
			using (var s = Container.GetContent(filename))
				if (s == null)
					throw new InvalidOperationException("Required file {0} not present in this map".F(filename));
		}

		// Stub constructor that doesn't produce a valid map, but is
		// sufficient for loading a mod to the content-install panel
		public Map() { }

		// The standard constructor for most purposes
		public Map(string path) : this(path, null) { }

		// Support upgrading format 5 maps to a more
		// recent version by defining upgradeForMod.
		public Map(string path, string upgradeForMod)
		{
			Path = path;
			Container = GlobalFileSystem.OpenPackage(path, null, int.MaxValue);

			AssertExists("map.yaml");
			AssertExists("map.bin");

			var yaml = new MiniYaml(null, MiniYaml.FromStream(Container.GetContent("map.yaml")));
			FieldLoader.Load(this, yaml);

			// Support for formats 1-3 dropped 2011-02-11.
			// Use release-20110207 to convert older maps to format 4
			// Use release-20110511 to convert older maps to format 5
			if (MapFormat < 5)
				throw new InvalidDataException("Map format {0} is not supported.\n File: {1}".F(MapFormat, path));

			// Format 5 -> 6 enforces the use of RequiresMod
			if (MapFormat == 5)
			{
				if (upgradeForMod == null)
					throw new InvalidDataException("Map format {0} is not supported, but can be upgraded.\n File: {1}".F(MapFormat, path));

				Console.WriteLine("Upgrading {0} from Format 5 to Format 6", path);

				// TODO: This isn't very nice, but there is no other consistent way
				// of finding the mod early during the engine initialization.
				RequiresMod = upgradeForMod;
			}

			var nd = yaml.ToDictionary();

			// Load players
			foreach (var my in nd["Players"].ToDictionary().Values)
			{
				var player = new PlayerReference(my);
				Players.Add(player.Name, player);
			}

			Actors = Exts.Lazy(() =>
			{
				var ret = new Dictionary<string, ActorReference>();
				foreach (var kv in nd["Actors"].ToDictionary())
					ret.Add(kv.Key, new ActorReference(kv.Value.Value, kv.Value.ToDictionary()));
				return ret;
			});

			// Smudges
			Smudges = Exts.Lazy(() =>
			{
				var ret = new List<SmudgeReference>();
				foreach (var name in nd["Smudges"].ToDictionary().Keys)
				{
					var vals = name.Split(' ');
					var loc = vals[1].Split(',');
					ret.Add(new SmudgeReference(vals[0], new int2(
							Exts.ParseIntegerInvariant(loc[0]),
							Exts.ParseIntegerInvariant(loc[1])),
							Exts.ParseIntegerInvariant(vals[2])));
				}

				return ret;
			});

			RuleDefinitions = MiniYaml.NodesOrEmpty(yaml, "Rules");
			SequenceDefinitions = MiniYaml.NodesOrEmpty(yaml, "Sequences");
			VoxelSequenceDefinitions = MiniYaml.NodesOrEmpty(yaml, "VoxelSequences");
			WeaponDefinitions = MiniYaml.NodesOrEmpty(yaml, "Weapons");
			VoiceDefinitions = MiniYaml.NodesOrEmpty(yaml, "Voices");
			NotificationDefinitions = MiniYaml.NodesOrEmpty(yaml, "Notifications");
			TranslationDefinitions = MiniYaml.NodesOrEmpty(yaml, "Translations");

			MapTiles = Exts.Lazy(() => LoadMapTiles());
			MapResources = Exts.Lazy(() => LoadResourceTiles());

			// The Uid is calculated from the data on-disk, so
			// format changes must be flushed to disk.
			// TODO: this isn't very nice
			if (MapFormat < 6)
				Save(path);

			Uid = ComputeHash();

			if (Container.Exists("map.png"))
				CustomPreview = new Bitmap(Container.GetContent("map.png"));

			PostInit();
		}

		void PostInit()
		{
			rules = Exts.Lazy(() => Game.modData.RulesetCache.LoadMapRules(this));

			var tl = new CPos(Bounds.Left, Bounds.Top);
			var br = new CPos(Bounds.Right - 1, Bounds.Bottom - 1);
			Cells = new CellRegion(tl, br);

			CustomTerrain = new CellLayer<int>(this);
			foreach (var cell in Cells)
				CustomTerrain[cell] = -1;
		}

		public Ruleset PreloadRules()
		{
			return rules.Value;
		}

		public CPos[] GetSpawnPoints()
		{
			return Actors.Value.Values
				.Where(a => a.Type == "mpspawn")
				.Select(a => (CPos)a.InitDict.Get<LocationInit>().value)
				.ToArray();
		}

		public void Save(string toPath)
		{
			MapFormat = 6;

			var root = new List<MiniYamlNode>();
			var fields = new[]
			{
				"Selectable",
				"MapFormat",
				"RequiresMod",
				"Title",
				"Description",
				"Author",
				"Tileset",
				"MapSize",
				"Bounds",
				"UseAsShellmap",
				"Type",
			};

			foreach (var field in fields)
			{
				var f = this.GetType().GetField(field);
				if (f.GetValue(this) == null)
					continue;
				root.Add(new MiniYamlNode(field, FieldSaver.FormatValue(this, f)));
			}

			root.Add(new MiniYamlNode("Options", FieldSaver.SaveDifferences(Options, new MapOptions())));

			root.Add(new MiniYamlNode("Players", null,
				Players.Select(p => new MiniYamlNode("PlayerReference@{0}".F(p.Key), FieldSaver.SaveDifferences(p.Value, new PlayerReference()))).ToList())
			);

			root.Add(new MiniYamlNode("Actors", null,
				Actors.Value.Select(x => new MiniYamlNode(x.Key, x.Value.Save())).ToList())
			);

			root.Add(new MiniYamlNode("Smudges", MiniYaml.FromList<SmudgeReference>(Smudges.Value)));
			root.Add(new MiniYamlNode("Rules", null, RuleDefinitions));
			root.Add(new MiniYamlNode("Sequences", null, SequenceDefinitions));
			root.Add(new MiniYamlNode("VoxelSequences", null, VoxelSequenceDefinitions));
			root.Add(new MiniYamlNode("Weapons", null, WeaponDefinitions));
			root.Add(new MiniYamlNode("Voices", null, VoiceDefinitions));
			root.Add(new MiniYamlNode("Notifications", null, NotificationDefinitions));
			root.Add(new MiniYamlNode("Translations", null, TranslationDefinitions));

			var entries = new Dictionary<string, byte[]>();
			entries.Add("map.bin", SaveBinaryData());
			var s = root.WriteToString();
			entries.Add("map.yaml", Encoding.UTF8.GetBytes(s));

			// Add any custom assets
			if (Container != null)
			{
				foreach (var file in Container.AllFileNames())
				{
					if (file == "map.bin" || file == "map.yaml")
						continue;

					entries.Add(file, Container.GetContent(file).ReadAllBytes());
				}
			}

			// Saving the map to a new location
			if (toPath != Path)
			{
				Path = toPath;

				// Create a new map package
				Container = GlobalFileSystem.CreatePackage(Path, int.MaxValue, entries);
			}

			// Update existing package
			Container.Write(entries);
		}

		public CellLayer<TerrainTile> LoadMapTiles()
		{
			var tiles = new CellLayer<TerrainTile>(this);
			using (var dataStream = Container.GetContent("map.bin"))
			{
				if (dataStream.ReadUInt8() != 1)
					throw new InvalidDataException("Unknown binary map format");

				// Load header info
				var width = dataStream.ReadUInt16();
				var height = dataStream.ReadUInt16();

				if (width != MapSize.X || height != MapSize.Y)
					throw new InvalidDataException("Invalid tile data");

				// Load tile data
				var data = dataStream.ReadBytes(MapSize.X * MapSize.Y * 3);
				var d = 0;
				for (var i = 0; i < MapSize.X; i++)
				{
					for (var j = 0; j < MapSize.Y; j++)
					{
						var tile = BitConverter.ToUInt16(data, d);
						d += 2;

						var index = data[d++];
						if (index == byte.MaxValue)
							index = (byte)(i % 4 + (j % 4) * 4);

						tiles[i, j] = new TerrainTile(tile, index);
					}
				}
			}

			return tiles;
		}

		public CellLayer<ResourceTile> LoadResourceTiles()
		{
			var resources = new CellLayer<ResourceTile>(this);

			using (var dataStream = Container.GetContent("map.bin"))
			{
				if (dataStream.ReadUInt8() != 1)
					throw new InvalidDataException("Unknown binary map format");

				// Load header info
				var width = dataStream.ReadUInt16();
				var height = dataStream.ReadUInt16();

				if (width != MapSize.X || height != MapSize.Y)
					throw new InvalidDataException("Invalid tile data");

				// Skip past tile data
				dataStream.Seek(3 * MapSize.X * MapSize.Y, SeekOrigin.Current);

				var data = dataStream.ReadBytes(MapSize.X * MapSize.Y * 2);
				var d = 0;

				// Load resource data
				for (var i = 0; i < MapSize.X; i++)
					for (var j = 0; j < MapSize.Y; j++)
						resources[i, j] = new ResourceTile(data[d++], data[d++]);
			}

			return resources;
		}

		public byte[] SaveBinaryData()
		{
			var dataStream = new MemoryStream();
			using (var writer = new BinaryWriter(dataStream))
			{
				// File header consists of a version byte, followed by 2 ushorts for width and height
				writer.Write(TileFormat);
				writer.Write((ushort)MapSize.X);
				writer.Write((ushort)MapSize.Y);

				// Tile data
				for (var i = 0; i < MapSize.X; i++)
					for (var j = 0; j < MapSize.Y; j++)
					{
						var tile = MapTiles.Value[new CPos(i, j)];
						writer.Write(tile.Type);
						writer.Write(tile.Index);
					}

				// Resource data
				for (var i = 0; i < MapSize.X; i++)
				{
					for (var j = 0; j < MapSize.Y; j++)
					{
						var tile = MapResources.Value[new CPos(i, j)];
						writer.Write(tile.Type);
						writer.Write(tile.Index);
					}
				}
			}

			return dataStream.ToArray();
		}

		public bool Contains(CPos cell)
		{
			return Bounds.Contains(cell.X, cell.Y);
		}

		public WPos CenterOfCell(CPos c)
		{
			return new WPos(1024 * c.X + 512, 1024 * c.Y + 512, 0);
		}

		public void Resize(int width, int height)		// editor magic.
		{
			var oldMapTiles = MapTiles.Value;
			var oldMapResources = MapResources.Value;
			var newSize = new Size(width, height);

			MapTiles = Exts.Lazy(() => CellLayer.Resize(oldMapTiles, newSize, oldMapTiles[0, 0]));
			MapResources = Exts.Lazy(() => CellLayer.Resize(oldMapResources, newSize, oldMapResources[0, 0]));
			MapSize = new int2(newSize);
		}

		public void ResizeCordon(int left, int top, int right, int bottom)
		{
			Bounds = Rectangle.FromLTRB(left, top, right, bottom);
			Cells = new CellRegion(new CPos(Bounds.Left, Bounds.Top), new CPos(Bounds.Right - 1, Bounds.Bottom - 1));
		}

		string ComputeHash()
		{
			// UID is calculated by taking an SHA1 of the yaml and binary data

			using (var ms = new MemoryStream())
			{
				// Read the relevant data into the buffer
				using (var s = Container.GetContent("map.yaml"))
					s.CopyTo(ms);
				using (var s = Container.GetContent("map.bin"))
					s.CopyTo(ms);

				// Take the SHA1
				ms.Seek(0, SeekOrigin.Begin);
				using (var csp = SHA1.Create())
					return new string(csp.ComputeHash(ms).SelectMany(a => a.ToString("x2")).ToArray());
			}
		}

		public void MakeDefaultPlayers()
		{
			var firstRace = Rules.Actors["world"].Traits
				.WithInterface<CountryInfo>().First(c => c.Selectable).Race;

			if (!Players.ContainsKey("Neutral"))
				Players.Add("Neutral", new PlayerReference
				{
					Name = "Neutral",
					Race = firstRace,
					OwnsWorld = true,
					NonCombatant = true
				});

			var numSpawns = GetSpawnPoints().Length;
			for (var index = 0; index < numSpawns; index++)
			{
				if (Players.ContainsKey("Multi{0}".F(index)))
					continue;

				var p = new PlayerReference
				{
					Name = "Multi{0}".F(index),
					Race = "Random",
					Playable = true,
					Enemies = new[] { "Creeps" }
				};
				Players.Add(p.Name, p);
			}

			Players.Add("Creeps", new PlayerReference
			{
				Name = "Creeps",
				Race = firstRace,
				NonCombatant = true,
				Enemies = Players.Where(p => p.Value.Playable).Select(p => p.Key).ToArray()
			});
		}

		public void FixOpenAreas(Ruleset rules)
		{
			var r = new Random();
			var tileset = rules.TileSets[Tileset];

			for (var j = Bounds.Top; j < Bounds.Bottom; j++)
			{
				for (var i = Bounds.Left; i < Bounds.Right; i++)
				{
					var cell = new CPos(i, j);
					var type = MapTiles.Value[cell].Type;
					var index = MapTiles.Value[cell].Index;
					if (!tileset.Templates.ContainsKey(type))
					{
						Console.WriteLine("Unknown Tile ID {0}".F(type));
						continue;
					}

					var template = tileset.Templates[type];
					if (!template.PickAny)
						continue;

					index = (byte)r.Next(0, template.TilesCount);
					MapTiles.Value[cell] = new TerrainTile(type, index);
				}
			}
		}

		public int GetTerrainIndex(CPos cell)
		{
			var custom = CustomTerrain[cell];
			var tileSet = Rules.TileSets[Tileset];
			return custom != -1 ? custom : tileSet.GetTerrainIndex(MapTiles.Value[cell]);
		}

		public TerrainTypeInfo GetTerrainInfo(CPos cell)
		{
			var tileSet = Rules.TileSets[Tileset];
			return tileSet[GetTerrainIndex(cell)];
		}

		public CPos Clamp(CPos xy)
		{
			var r = Bounds;
			return xy.Clamp(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1));
		}

		public CPos ChooseRandomCell(MersenneTwister rand)
		{
			return new CPos(
				rand.Next(Bounds.Left, Bounds.Right),
				rand.Next(Bounds.Top, Bounds.Bottom));
		}

		public CPos ChooseRandomEdgeCell(MersenneTwister rand)
		{
			var isX = rand.Next(2) == 0;
			var edge = rand.Next(2) == 0;

			return new CPos(
				isX ? rand.Next(Bounds.Left, Bounds.Right) : (edge ? Bounds.Left : Bounds.Right),
				!isX ? rand.Next(Bounds.Top, Bounds.Bottom) : (edge ? Bounds.Top : Bounds.Bottom));
		}

		public WRange DistanceToEdge(WPos pos, WVec dir)
		{
			var tl = CenterOfCell(new CPos(Bounds.Left, Bounds.Top)) - new WVec(512, 512, 0);
			var br = CenterOfCell(new CPos(Bounds.Right, Bounds.Bottom)) + new WVec(511, 511, 0);
			var x = dir.X == 0 ? int.MaxValue : ((dir.X < 0 ? tl.X : br.X) - pos.X) / dir.X;
			var y = dir.Y == 0 ? int.MaxValue : ((dir.Y < 0 ? tl.Y : br.Y) - pos.Y) / dir.Y;
			return new WRange(Math.Min(x, y) * dir.Length);
		}

		public const int MaxTilesInCircleRange = 50;
		static List<CVec>[] TilesByDistance = InitTilesByDistance(MaxTilesInCircleRange);

		static List<CVec>[] InitTilesByDistance(int max)
		{
			var ts = new List<CVec>[max + 1];
			for (var i = 0; i < max + 1; i++)
				ts [i] = new List<CVec>();

			for (var j = -max; j <= max; j++)
				for (var i = -max; i <= max; i++)
					if (max * max >= i * i + j * j)
						ts [(int)Math.Ceiling(Math.Sqrt(i * i + j * j))].Add(new CVec(i, j));

			return ts;
		}

		public IEnumerable<CPos> FindTilesInCircle(CPos center, int range)
		{
			if (range >= TilesByDistance.Length)
				throw new InvalidOperationException("FindTilesInCircle supports queries for only <= {0}".F(MaxTilesInCircleRange));

			for (var i = 0; i <= range; i++)
			{
				foreach (var offset in TilesByDistance[i])
				{
					var t = offset + center;
					if (Contains(t))
						yield return t;
				}
			}
		}
	}
}
