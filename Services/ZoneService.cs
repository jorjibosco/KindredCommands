using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using ProjectM.Terrain;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace KindredCommands.Services;

internal class ZoneService
{
	static readonly string CONFIG_PATH = Path.Combine(BepInEx.Paths.ConfigPath, MyPluginInfo.PLUGIN_NAME);
	static readonly string ZONES_PATH = Path.Combine(CONFIG_PATH, "zones.json");

	Dictionary<string, (string ZoneName, int Level)> gatedZones = [];

	Dictionary<string, (Vector3 Center, float Radius, int Level)> gatedRadiusZones = [];

	Dictionary<Entity, Vector3> lastValidPos = [];
	Dictionary<Entity, float> lastSentMessage = [];

	public IEnumerable<KeyValuePair<string, (string ZoneName, int Level)>> GatedZones => gatedZones;
	public IEnumerable<KeyValuePair<string, (Vector3 Center, float Radius, int Level)>> GatedRadiusZones => gatedRadiusZones;

	public class GameZone
	{
		public string Name { get; set; }
		public List<float2> Vertices { get; set; } = new();

		public bool ContainsPoint(float px, float pz)
		{
			int n = Vertices.Count;
			if (n < 3) return false;
			bool inside = false;
			int j = n - 1;
			for (int i = 0; i < n; i++)
			{
				float xi = Vertices[i].x, zi = Vertices[i].y;
				float xj = Vertices[j].x, zj = Vertices[j].y;
				if (((zi > pz) != (zj > pz)) && (px < (xj - xi) * (pz - zi) / (zj - zi) + xi))
					inside = !inside;
				j = i;
			}
			return inside;
		}
	}

	struct Bounds2D
	{
		public float MinX, MaxX, MinZ, MaxZ;

		public bool Contains(float x, float z) =>
			x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

		public static Bounds2D FromVertices(List<float2> verts)
		{
			var b = new Bounds2D { MinX = float.MaxValue, MaxX = float.MinValue, MinZ = float.MaxValue, MaxZ = float.MinValue };
			foreach (var v in verts)
			{
				if (v.x < b.MinX) b.MinX = v.x;
				if (v.x > b.MaxX) b.MaxX = v.x;
				if (v.y < b.MinZ) b.MinZ = v.y;
				if (v.y > b.MaxZ) b.MaxZ = v.y;
			}
			return b;
		}

		public static Bounds2D FromRadius(Vector3 center, float radius) => new()
		{
			MinX = center.x - radius,
			MaxX = center.x + radius,
			MinZ = center.z - radius,
			MaxZ = center.z + radius
		};
	}

	class GatedPolygonEntry
	{
		public string FriendlyName;
		public int Level;
		public GameZone Zone;
		public Bounds2D Bounds;
	}

	class GatedRadiusEntry
	{
		public string FriendlyName;
		public Vector3 Center;
		public float RadiusSq;
		public int Level;
		public Bounds2D Bounds;
	}

	List<GatedPolygonEntry> gatedPolygonCache = new();
	List<GatedRadiusEntry> gatedRadiusCache = new();

	List<GameZone> loadedZones = new();
	Dictionary<string, GameZone> loadedZonesByName = new();
	bool zonesLoaded = false;

	struct ZoneFile
	{
		public Dictionary<string, string> GatedZoneNames { get; set; }
		public Dictionary<string, int> GatedZoneLevels { get; set; }
		public Dictionary<string, float[]> GatedRadiusCenters { get; set; }
		public Dictionary<string, float> GatedRadiusValues { get; set; }
		public Dictionary<string, int> GatedRadiusLevels { get; set; }
	}

	public ZoneService()
	{
		LoadZones();
		Core.StartCoroutine(InitZoneData());
		Core.StartCoroutine(CheckPlayerZones());
	}

	IEnumerator InitZoneData()
	{
		while (!zonesLoaded)
		{
			var em = Core.EntityManager;
			var query = em.CreateEntityQuery(
				ComponentType.ReadOnly<MapZoneData>(),
				ComponentType.ReadOnly<MapZonePolygonVertexElement>()
			);
			var entities = query.ToEntityArray(Allocator.Temp);

			if (entities.Length > 0)
			{
				loadedZones.Clear();
				loadedZonesByName.Clear();
				foreach (var entity in entities)
				{
					try
					{
						var zoneData = em.GetComponentData<MapZoneData>(entity);
						var vertexBuffer = em.GetBuffer<MapZonePolygonVertexElement>(entity);
						var zone = new GameZone { Name = zoneData.ZoneId.ToString() };
						for (int i = 0; i < vertexBuffer.Length; i++)
							zone.Vertices.Add(vertexBuffer[i].VertexPos);
						loadedZones.Add(zone);
						loadedZonesByName[zone.Name] = zone;
					}
					catch (Exception e)
					{
						Core.Log.LogError($"[ZoneService] Error loading zone: {e.Message}");
					}
				}
				entities.Dispose();
				query.Dispose();
				zonesLoaded = true;
				Core.Log.LogInfo($"[ZoneService] Loaded {loadedZones.Count} map zones.");
				BuildGateCache();
			}
			else
			{
				entities.Dispose();
				query.Dispose();
			}
			yield return null;
		}
	}

	void BuildGateCache()
	{
		gatedPolygonCache.Clear();
		foreach (var kvp in gatedZones)
		{
			if (!loadedZonesByName.TryGetValue(kvp.Value.ZoneName, out var zone))
				continue;

			gatedPolygonCache.Add(new GatedPolygonEntry
			{
				FriendlyName = kvp.Key,
				Level = kvp.Value.Level,
				Zone = zone,
				Bounds = Bounds2D.FromVertices(zone.Vertices)
			});
		}

		gatedRadiusCache.Clear();
		foreach (var kvp in gatedRadiusZones)
		{
			gatedRadiusCache.Add(new GatedRadiusEntry
			{
				FriendlyName = kvp.Key,
				Center = kvp.Value.Center,
				RadiusSq = kvp.Value.Radius * kvp.Value.Radius,
				Level = kvp.Value.Level,
				Bounds = Bounds2D.FromRadius(kvp.Value.Center, kvp.Value.Radius)
			});
		}
	}

	void LoadZones()
	{
		if (!File.Exists(ZONES_PATH)) return;

		var json = File.ReadAllText(ZONES_PATH);
		var zoneFile = JsonSerializer.Deserialize<ZoneFile>(json);

		gatedZones.Clear();
		if (zoneFile.GatedZoneNames != null && zoneFile.GatedZoneLevels != null)
		{
			foreach (var kvp in zoneFile.GatedZoneNames)
			{
				if (zoneFile.GatedZoneLevels.TryGetValue(kvp.Key, out var level))
					gatedZones[kvp.Key] = (kvp.Value, level);
			}
		}

		gatedRadiusZones.Clear();
		if (zoneFile.GatedRadiusCenters != null && zoneFile.GatedRadiusValues != null && zoneFile.GatedRadiusLevels != null)
		{
			foreach (var kvp in zoneFile.GatedRadiusCenters)
			{
				if (zoneFile.GatedRadiusValues.TryGetValue(kvp.Key, out var radius) &&
					zoneFile.GatedRadiusLevels.TryGetValue(kvp.Key, out var level) &&
					kvp.Value.Length == 3)
				{
					var center = new Vector3(kvp.Value[0], kvp.Value[1], kvp.Value[2]);
					gatedRadiusZones[kvp.Key] = (center, radius, level);
				}
			}
		}

		BuildGateCache();
	}

	void SaveZones()
	{
		if (!Directory.Exists(CONFIG_PATH)) Directory.CreateDirectory(CONFIG_PATH);

		var zoneFile = new ZoneFile
		{
			GatedZoneNames = gatedZones.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ZoneName),
			GatedZoneLevels = gatedZones.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Level),
			GatedRadiusCenters = gatedRadiusZones.ToDictionary(
				kvp => kvp.Key,
				kvp => new float[] { kvp.Value.Center.x, kvp.Value.Center.y, kvp.Value.Center.z }),
			GatedRadiusValues = gatedRadiusZones.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Radius),
			GatedRadiusLevels = gatedRadiusZones.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Level)
		};

		var options = new JsonSerializerOptions { WriteIndented = true };
		var json = JsonSerializer.Serialize(zoneFile, options);
		File.WriteAllText(ZONES_PATH, json);

		BuildGateCache();
	}

	public GameZone GetCurrentZone(float3 pos)
	{
		foreach (var zone in loadedZones)
			if (zone.ContainsPoint(pos.x, pos.z))
				return zone;
		return null;
	}

	public string GetCurrentZoneName(float3 pos) => GetCurrentZone(pos)?.Name;

	public bool GateZone(string friendlyName, float3 adminPos, int level)
	{
		var zone = GetCurrentZone(adminPos);
		if (zone == null) return false;
		gatedZones[friendlyName] = (zone.Name, level);
		SaveZones();
		return true;
	}

	public bool UngateZone(string friendlyName)
	{
		var result = gatedZones.Remove(friendlyName);
		if (!result) result = gatedRadiusZones.Remove(friendlyName);
		SaveZones();
		return result;
	}

	public void GateRadiusZone(string friendlyName, Vector3 center, float radius, int level)
	{
		gatedRadiusZones[friendlyName] = (center, radius, level);
		SaveZones();
	}

	string DisallowedAtPosition(Vector3 pos, int maxLevel, out string zoneKeyHit)
	{
		foreach (var entry in gatedPolygonCache)
		{
			if (!entry.Bounds.Contains(pos.x, pos.z)) continue;
			if (!entry.Zone.ContainsPoint(pos.x, pos.z)) continue;
			if (maxLevel < entry.Level)
			{
				zoneKeyHit = entry.FriendlyName;
				return $"Can't enter zone <color=#F54927>{entry.FriendlyName}</color> - requires level <color=#F54927>{entry.Level}</color>\n(Your max level reached is {maxLevel})";
			}
			zoneKeyHit = entry.FriendlyName;
			return null;
		}

		foreach (var entry in gatedRadiusCache)
		{
			if (!entry.Bounds.Contains(pos.x, pos.z)) continue;
			var dx = pos.x - entry.Center.x;
			var dz = pos.z - entry.Center.z;
			if (dx * dx + dz * dz > entry.RadiusSq) continue;
			if (maxLevel < entry.Level)
			{
				zoneKeyHit = entry.FriendlyName;
				return $"Can't enter zone <color=#F54927>{entry.FriendlyName}</color> - requires level <color=#F54927>{entry.Level}</color>\n(Your max level reached is {maxLevel})";
			}
			zoneKeyHit = entry.FriendlyName;
			return null;
		}

		zoneKeyHit = null;
		return null;
	}

	IEnumerator CheckPlayerZones()
	{
		while (true)
		{
			if (!zonesLoaded) { yield return null; continue; }

			foreach (var userEntity in Core.Players.GetCachedUsersOnline())
			{
				if (!userEntity.Has<User>()) continue;

				var charName = userEntity.Read<User>().CharacterName.ToString();
				if (String.IsNullOrEmpty(charName)) continue;

				var charEntity = userEntity.Read<User>().LocalCharacter.GetEntityOnServer();
				if (!charEntity.Has<Equipment>()) continue;

				var pos = charEntity.Read<Translation>().Value;
				var maxLevel = Core.Regions.GetPlayerMaxLevel(charName);

				var returnReason = DisallowedAtPosition(pos, maxLevel, out var zoneKeyHit);

				if (returnReason != null)
				{
					ReturnPlayer(userEntity, charEntity, returnReason, maxLevel);
				}
				else if (charEntity.Has<Dead>())
				{
					lastValidPos.Remove(userEntity);
				}
				else
				{
					lastValidPos[userEntity] = pos;
				}
			}
			yield return null;
		}
	}

	void ReturnPlayer(Entity userEntity, Entity charEntity, string returnReason, int maxLevel)
	{
		var returnPos = Vector3.zero;

		if (lastValidPos.TryGetValue(userEntity, out var lastValid) &&
			DisallowedAtPosition(lastValid, maxLevel, out _) == null)
		{
			returnPos = lastValid;
		}

		if (returnPos == Vector3.zero)
		{
			var waypoints = Helper.GetEntitiesByComponentType<ChunkWaypoint>();
			var waypointArray = waypoints.ToArray();
			waypoints.Dispose();

			returnPos = waypointArray
				.Where(x =>
				{
					if (!x.Has<UserOwner>()) return true;
					var owner = x.Read<UserOwner>().Owner.GetEntityOnServer();
					return owner == Entity.Null || owner == userEntity;
				})
				.Select(x => x.Read<Translation>().Value)
				.OrderBy(wp => Vector3.Distance(wp, charEntity.Read<Translation>().Value))
				.Where(wp => DisallowedAtPosition(wp, maxLevel, out _) == null)
				.FirstOrDefault();
		}

		if (!lastSentMessage.TryGetValue(userEntity, out var lastSent) || lastSent + 10 < Time.time)
		{
			FixedString512Bytes message = returnReason;
			ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, userEntity.Read<User>(), ref message);
			lastSentMessage[userEntity] = Time.time;
		}

		charEntity.Write(new Translation { Value = returnPos });
		charEntity.Write(new LastTranslation { Value = returnPos });
	}
}
