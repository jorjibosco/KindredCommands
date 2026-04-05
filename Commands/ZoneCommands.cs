using System.Linq;
using System.Text;
using Unity.Transforms;
using UnityEngine;
using VampireCommandFramework;

namespace KindredCommands.Commands;

[CommandGroup("zone")]
internal class ZoneCommands
{
	[Command("name", "n", description: "Shows the zone ID you are currently standing in.", adminOnly: true)]
	public static void ZoneNameCommand(ChatCommandContext ctx)
	{
		var charEntity = ctx.Event.SenderCharacterEntity;
		var pos = charEntity.Read<Translation>().Value;
		var zoneName = Core.Zones.GetCurrentZoneName(pos);

		if (zoneName == null)
			ctx.Reply("You are not standing in any named zone.");
		else
			ctx.Reply($"Current zone ID: <color=#00FFBB>{zoneName}</color>");
	}

	[Command("gate", "g", description: "Gates the zone you are standing in with a friendly name and minimum level.", adminOnly: true)]
	public static void GateZoneCommand(ChatCommandContext ctx, string friendlyName, int level)
	{
		var charEntity = ctx.Event.SenderCharacterEntity;
		var pos = charEntity.Read<Translation>().Value;

		if (Core.Zones.GateZone(friendlyName, pos, level))
		{
			var zoneName = Core.Zones.GetCurrentZoneName(pos);
			ctx.Reply($"Gated zone <color=white>{friendlyName}</color> (ID: {zoneName}) — minimum level: <color=#00FFBB>{level}</color>");
		}
		else
		{
			ctx.Reply("You are not standing in any named zone. Move into the zone you want to gate first.");
		}
	}

	[Command("gateradius", "gr", description: "Gates a circular area centered on your position with a given radius and minimum level.", adminOnly: true)]
	public static void GateRadiusZoneCommand(ChatCommandContext ctx, string friendlyName, int level, float radius)
	{
		var charEntity = ctx.Event.SenderCharacterEntity;
		var pos = charEntity.Read<Translation>().Value;

		Core.Zones.GateRadiusZone(friendlyName, pos, radius, level);
		ctx.Reply($"Gated radius zone <color=white>{friendlyName}</color> — radius: <color=#00FFBB>{radius}</color>, minimum level: <color=#00FFBB>{level}</color>");
	}

	[Command("ungate", "ug", description: "Removes the gate from a zone by its friendly name (works for both zone and radius gates).", adminOnly: true)]
	public static void UngateZoneCommand(ChatCommandContext ctx, string friendlyName)
	{
		if (Core.Zones.UngateZone(friendlyName))
			ctx.Reply($"Removed gate from zone <color=#00FFBB>{friendlyName}</color>.");
		else
			ctx.Reply($"No gated zone found with the name <color=#00FFBB>{friendlyName}</color>.");
	}

	[Command("list", "l", description: "Lists all gated zones and their level requirements.", adminOnly: false)]
	public static void ListZonesCommand(ChatCommandContext ctx)
	{
		var gatedZones = Core.Zones.GatedZones.ToList();
		var gatedRadiusZones = Core.Zones.GatedRadiusZones.ToList();

		if (!gatedZones.Any() && !gatedRadiusZones.Any())
		{
			ctx.Reply("<color=#F54927>No zones</color> are currently gated.");
			return;
		}

		var sb = new StringBuilder();
		sb.AppendLine("<color=#00FFBB>— Gated Zones & Lv Requirements —</color>");

		foreach (var kvp in gatedZones)
		{
			var line = $"<color=white>{kvp.Key}</color> (Lv {kvp.Value.Level})";
			if (sb.Length + line.Length > Core.MAX_REPLY_LENGTH)
			{
				ctx.Reply(sb.ToString());
				sb.Clear();
			}
			sb.AppendLine(line);
		}

		foreach (var kvp in gatedRadiusZones)
		{
			var line = $"<color=white>{kvp.Key}</color> (Lv {kvp.Value.Level})";
			if (sb.Length + line.Length > Core.MAX_REPLY_LENGTH)
			{
				ctx.Reply(sb.ToString());
				sb.Clear();
			}
			sb.AppendLine(line);
		}

		ctx.Reply(sb.ToString());
	}
}
