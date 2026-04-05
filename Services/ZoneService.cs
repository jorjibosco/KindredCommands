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

	// Polygon gated zones: friendly name -> (zoneName, minLevel)
	Dictionary<string, (string ZoneName, int Level)> gatedZones = [];

	// Radius gated zones: friendly name -> (center, radius, minLevel)
	Dictionary<string, (Vector3 Center, float Radius, int Level)> gatedRadiusZones = [];

	Dictionary<Entity, (string ZoneName, Vector3 Position)> lastValidPos = [];
	Dictionary<Entity, float> lastSentMessage = [];
	Dictionary<Entity, float> lastReturnTime = [];

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

	List<GameZone> loadedZones = new();
	bool zonesLoaded = false;

	struct ZoneFile
	{
		public Dictionary<string, string> GatedZoneNames { get; set; }
		public Dictionary<string, int> GatedZoneLevels { get; set; }
		public Dictionary<string, float[]> GatedRadiusCenters { get; set; }   // float[3] x,y,z
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
			}
			else
			{
				entities.Dispose();
				query.Dispose();
			}
			yield return null;
		}
	}

	void LoadZones()
	{
		if (!File.Exists(ZONES_PATH)) return;

		var json = File.ReadAllText(ZONES_PATH);
		var options = new JsonSerializerOptions { WriteIndented = true };
		var zoneFile = JsonSerializer.Deserialize<ZoneFile>(json, options);

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
	}

	public GameZone GetCurrentZone(float3 pos)
	{
		foreach (var zone in loadedZones)
			if (zone.ContainsPoint(pos.x, pos.z))
				return zone;
		return null;
	}

	public string GetCurrentZoneName(float3 pos) => GetCurrentZone(pos)?.Name;

	// Check if pos is inside any radius gate, returns the friendly name or null
	string GetCurrentRadiusZoneName(Vector3 pos)
	{
		foreach (var kvp in gatedRadiusZones)
		{
			var flat = new Vector2(pos.x - kvp.Value.Center.x, pos.z - kvp.Value.Center.z);
			if (flat.magnitude <= kvp.Value.Radius)
				return kvp.Key;
		}
		return null;
	}

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
				var currentZoneName = GetCurrentZoneName(pos);
				var currentRadiusZoneName = GetCurrentRadiusZoneName(pos);
				var maxLevel = Core.Regions.GetPlayerMaxLevel(charName);

				var returnReason = DisallowedFromZone(charName, currentZoneName, maxLevel)
								?? DisallowedFromRadiusZone(charName, currentRadiusZoneName, maxLevel);

				if (returnReason != null)
				{
					ReturnPlayer(userEntity, charEntity, returnReason);
				}
				else if (charEntity.Has<Dead>())
				{
					lastValidPos.Remove(userEntity);
				}
				else
				{
					lastValidPos[userEntity] = (currentZoneName ?? "", charEntity.Read<Translation>().Value);
				}

				yield return null;
			}
			yield return null;
		}
	}

	string DisallowedFromZone(string charName, string zoneName, int maxLevel)
	{
		if (zoneName == null) return null;
		foreach (var kvp in gatedZones)
		{
			if (kvp.Value.ZoneName == zoneName && maxLevel < kvp.Value.Level)
				return $"Can't enter zone <color=#F54927>{kvp.Key}</color> - requires level <color=#F54927>{kvp.Value.Level}</color>\n(Your max level reached is {maxLevel})";
		}
		return null;
	}

	string DisallowedFromRadiusZone(string charName, string radiusZoneName, int maxLevel)
	{
		if (radiusZoneName == null) return null;
		if (gatedRadiusZones.TryGetValue(radiusZoneName, out var gate) && maxLevel < gate.Level)
			return $"Can't enter zone <color=#F54927>{radiusZoneName}</color> - requires level <color=#F54927>{gate.Level}</color>\n(Your max level reached is {maxLevel})";
		return null;
	}

	void ReturnPlayer(Entity userEntity, Entity charEntity, string returnReason)
	{
		var returnPos = Vector3.zero;
		var charName = userEntity.Read<User>().CharacterName.ToString();
		var maxLevel = Core.Regions.GetPlayerMaxLevel(charName);

		if (lastValidPos.TryGetValue(userEntity, out var lastValid))
		{
			if (DisallowedFromZone(charName, lastValid.ZoneName, maxLevel) == null &&
				DisallowedFromRadiusZone(charName, GetCurrentRadiusZoneName(lastValid.Position), maxLevel) == null)
				returnPos = lastValid.Position;
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
				.Where(wp =>
				{
					var zoneName = GetCurrentZoneName(new float3(wp.x, wp.y, wp.z));
					var radiusZoneName = GetCurrentRadiusZoneName(wp);
					return DisallowedFromZone(charName, zoneName, maxLevel) == null &&
						   DisallowedFromRadiusZone(charName, radiusZoneName, maxLevel) == null;
				})
				.FirstOrDefault();
		}

		if (!lastSentMessage.TryGetValue(userEntity, out var lastSent) || lastSent + 10 < Time.time)
		{
			FixedString512Bytes message = returnReason;
			ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, userEntity.Read<User>(), ref message);
			lastSentMessage[userEntity] = Time.time;
		}

		if (!lastReturnTime.TryGetValue(userEntity, out var lastReturn) || lastReturn + 1.0f < Time.time)
		{
			charEntity.Write(new Translation { Value = returnPos });
			charEntity.Write(new LastTranslation { Value = returnPos });
			lastReturnTime[userEntity] = Time.time;
		}
	}
}
