﻿//reference System.Core.dll
	
/* NOTE:
 	- You need to replace all "NameOfGamemode" strings with the name of your gamemode. E.g "SkyWars", "TNTRun" etc
 	- You need to replace all "NOG" strings with the name of your gamemode. E.g "SW", "TRUN" etc
 	
	^ Easiest way is CTRL + H in most text/code editors.
 	
 	- To add maps, you will need to type /sw add.
 	- If you want to use my custom spawn thing, you will need to:
		1. Uncomment the comments on lines 197 and 235
		2. Add all of the spawn coords to a [lowercase gamemode name].txt file (make sure all spawns are on a new line)
 		3. Upload the .txt file into ./plugins/NameOfGamemode/
 		4. /server reload
*/ 
	
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

using MCGalaxy.Commands;
using MCGalaxy.Commands.Fun;
using MCGalaxy.Config;
using MCGalaxy.Events.PlayerEvents;
using MCGalaxy.Maths;
using MCGalaxy.Network;
using BlockID = System.UInt16;

namespace MCGalaxy.Games {

	public class NOGMapConfig {
		[ConfigVec3("nog-spawn", null)]
		public Vec3U16 Spawn;
		
		static string Path(string map) { return "./plugins/NameOfGamemode/maps" + map + ".config"; }
		static ConfigElement[] cfg;
		
		public void SetDefaults(Level lvl) {
			Spawn.X = (ushort)(lvl.Width  / 2);
			Spawn.Y = (ushort)(lvl.Height / 2 + 1);
			Spawn.Z = (ushort)(lvl.Length / 2);
		}
		
		public void Load(string map) {
			if (cfg == null) cfg = ConfigElement.GetAll(typeof(NOGMapConfig));
			ConfigElement.ParseFile(cfg, Path(map), this);
		}
		
		public void Save(string map) {
			if (cfg == null) cfg = ConfigElement.GetAll(typeof(NOGMapConfig));
			ConfigElement.SerialiseSimple(cfg, Path(map), this);
		}
	}
	
	public sealed class NameOfGamemodePlugin : Plugin {
		public override string creator { get { return "Venk"; } }
		public override string MCGalaxy_Version { get { return "1.9.1.5"; } }
		public override string name { get { return "NameOfGamemode"; } }
		
		Command cmd;
		public override void Load(bool startup) {
			cmd = new CmdNameOfGamemode();
			Command.Register(cmd);
			
			RoundsGame game = NOGGame.Instance;
			game.GetConfig().Load();
			if (!game.Running) game.AutoStart();
		}
		
		public override void Unload(bool shutdown) {
			Command.Unregister(cmd);
			RoundsGame game = NOGGame.Instance;
			if (game.Running) game.End();
		}
	}
	
	public sealed class NOGConfig : RoundsGameConfig {
		public override bool AllowAutoload { get { return true; } }
		protected override string GameName { get { return "NameOfGamemode"; } }
		protected override string PropsPath { get { return "./plugins/NameOfGamemode/game.properties"; } }
	}
	
	public sealed partial class NOGGame : RoundsGame {
		public VolatileArray<Player> Alive = new VolatileArray<Player>();
		
		public static NOGGame Instance = new NOGGame();
		public NOGGame() { Picker = new LevelPicker(); }
		
		public static NOGConfig Config = new NOGConfig();
		public override RoundsGameConfig GetConfig() { return Config; }
		
		public override string GameName { get { return "NameOfGamemode"; } }
		public int Interval = 150;
		public NOGMapConfig cfg = new NOGMapConfig();
		
		// ============================================ GAME =======================================
		public override void UpdateMapConfig() {
			cfg = new NOGMapConfig();
			cfg.SetDefaults(Map);
			cfg.Load(Map.name);
		}
		
		protected override List<Player> GetPlayers() {
			return Map.getPlayers();
		}
		
		public override void OutputStatus(Player p) {
			Player[] alive = Alive.Items;
			p.Message("Alive players: " + alive.Join(pl => pl.ColoredName));
		}

		public override void Start(Player p, string map, int rounds) {
			// Starts on current map by default
			if (!p.IsSuper && map.Length == 0) map = p.level.name;
			base.Start(p, map, rounds);
		}

		protected override void StartGame() { Config.Load(); }
		
		protected override void EndGame() {
			if (RoundInProgress) EndRound(null);
			Alive.Clear();
		}
		
		public override void PlayerLeftGame(Player p) { // "kill" player if they leave server or change map
			Alive.Remove(p);
			UpdatePlayersLeft();
		}
		
		protected override string FormatStatus1(Player p) {
			return RoundInProgress ? "%b" + Alive.Count + " %Splayers left" : "";
		}
		
		// ============================================ PLUGIN =======================================		
		protected override void HookEventHandlers() {
			OnPlayerSpawningEvent.Register(HandlePlayerSpawning, Priority.High);
			OnJoinedLevelEvent.Register(HandleOnJoinedLevel, Priority.High);
			OnPlayerChatEvent.Register(HandlePlayerChat, Priority.High);
			
			base.HookEventHandlers();
		}
		
		protected override void UnhookEventHandlers() {
			OnPlayerSpawningEvent.Unregister(HandlePlayerSpawning);
			OnJoinedLevelEvent.Unregister(HandleOnJoinedLevel);
			OnPlayerChatEvent.Unregister(HandlePlayerChat);
			
			base.UnhookEventHandlers();
		}
		
		// Checks if player votes for a map when voting in progress "1, 2, 3"
		void HandlePlayerChat(Player p, string message) {
			if (Picker.HandlesMessage(p, message)) { p.cancelchat = true; return; }
		}
		
		// This event is called when a player is killed
		void HandlePlayerSpawning(Player p, ref Position pos, ref byte yaw, ref byte pitch, bool respawning) {
			if (!respawning || !Alive.Contains(p)) return;
			if (p.Game.Referee) return;
			if (p.level != Map) return;
			
			Alive.Remove(p); // Remove them from the alive list
			UpdatePlayersLeft();
			p.Game.Referee = true; // This allows them to fly and noclip when they die
			p.Send(Packet.HackControl(true, true, true, true, true, -1)); // ^
			Entities.GlobalDespawn(p, true); // Remove from tab list
			Server.hidden.AddUnique(p.name); // Hides the player
		}
		
		// We use this event for resetting everything and preparing for the next map
		void HandleOnJoinedLevel(Player p, Level prevLevel, Level level, ref bool announce) {
			p.Extras.Remove("NOG_INDEX");
			HandleJoinedCommon(p, prevLevel, level, ref announce);
			Entities.GlobalSpawn(p, true); // Adds player back to the tab list
			Server.hidden.Remove(p.name); // Unhides the player
			
			if (level == Map) {
				// Revert back to -hax
				p.Game.Referee = false;
				p.Send(Packet.Motd(p, "-hax -push"));
				p.invincible = true;
				
				if (Running) {
					if (RoundInProgress) {
						// Force spectator mode if they join late
						p.Game.Referee = true;
						p.Send(Packet.HackControl(true, true, true, true, true, -1));
						p.Message("You joined in the middle of the round so you are now a spectator.");
						return; 
					}
					
					else {
						List<Player> players = level.getPlayers();
						/* int index = players.Count;
						p.Message("you are p" + index); // Debugging
						p.Extras["NOG_INDEX"] = index;
						int num = p.Extras.GetInt("NOG_INDEX");
						
						string file = "plugins/NameOfGamemode/" + p.level.name + ".txt";
						string spawn1 = File.ReadLines(file).Skip(0).Take(1).First();
						string spawn2 = File.ReadLines(file).Skip(1).Take(1).First();
						string spawn3 = File.ReadLines(file).Skip(2).Take(1).First();
						string spawn4 = File.ReadLines(file).Skip(3).Take(1).First();
						string spawn5 = File.ReadLines(file).Skip(4).Take(1).First();
						string spawn6 = File.ReadLines(file).Skip(5).Take(1).First();
						string spawn7 = File.ReadLines(file).Skip(6).Take(1).First();
						string spawn8 = File.ReadLines(file).Skip(7).Take(1).First();
						
						if (num == 1) { Command.Find("TP").Use(p, spawn1); }
						if (num == 2) { Command.Find("TP").Use(p, spawn2); }
						if (num == 3) { Command.Find("TP").Use(p, spawn3); }
						if (num == 4) { Command.Find("TP").Use(p, spawn4); }
						if (num == 5) { Command.Find("TP").Use(p, spawn5); }
						if (num == 6) { Command.Find("TP").Use(p, spawn6); }
						if (num == 7) { Command.Find("TP").Use(p, spawn7); }
						if (num == 8) { Command.Find("TP").Use(p, spawn8); }
						
						// Bold text on my server, remove if you want
						string mapName = Map.name.Replace('a', '◘').Replace('b', '○').Replace('c', '◙').Replace('d', '♂').Replace('e', '♀').Replace('f', '♪').Replace('g', '♫').Replace('h', '☼').Replace('i', '↔').Replace('j', '▲').Replace('k', '▼').Replace('l', '¢').Replace('m', '£').Replace('n', '¥').Replace('o', '₧').Replace('p', 'ƒ').Replace('q', 'æ').Replace('r', 'Æ').Replace('s', 'ª').Replace('t', 'º').Replace('u', '¿').Replace('v', '⌐').Replace('w', '¬').Replace('x', '½').Replace('y', '¼').Replace('z', '¡').Replace('_', ' ').Replace('A', 'α').Replace('B', 'ß').Replace('C', 'Γ').Replace('D', 'π').Replace('E', 'Σ').Replace('F', 'σ').Replace('G', 'µ').Replace('H', 'τ').Replace('I', 'Φ').Replace('J', 'Θ').Replace('K', 'Ω').Replace('L', 'δ').Replace('M', '∞').Replace('N', 'φ').Replace('O', 'ε').Replace('P', '∩').Replace('Q', '≡').Replace('R', '±').Replace('S', '≥').Replace('T', '≤').Replace('U', '⌠').Replace('V', '⌡').Replace('W', '÷').Replace('X', '≈').Replace('Y', '°').Replace('Z', '√');
			
						string authors = Map.Config.Authors;
		
						string mapAuthors = authors.Replace('a', '◘').Replace('b', '○').Replace('c', '◙').Replace('d', '♂').Replace('e', '♀').Replace('f', '♪').Replace('g', '♫').Replace('h', '☼').Replace('i', '↔').Replace('j', '▲').Replace('k', '▼').Replace('l', '¢').Replace('m', '£').Replace('n', '¥').Replace('o', '₧').Replace('p', 'ƒ').Replace('q', 'æ').Replace('r', 'Æ').Replace('s', 'ª').Replace('t', 'º').Replace('u', '¿').Replace('v', '⌐').Replace('w', '¬').Replace('x', '½').Replace('y', '¼').Replace('z', '¡').Replace(",", "%2,%a ").Replace('A', 'α').Replace('B', 'ß').Replace('C', 'Γ').Replace('D', 'π').Replace('E', 'Σ').Replace('F', 'σ').Replace('G', 'µ').Replace('H', 'τ').Replace('I', 'Φ').Replace('J', 'Θ').Replace('K', 'Ω').Replace('L', 'δ').Replace('M', '∞').Replace('N', 'φ').Replace('O', 'ε').Replace('P', '∩').Replace('Q', '≡').Replace('R', '±').Replace('S', '≥').Replace('T', '≤').Replace('U', '⌠').Replace('V', '⌡').Replace('W', '÷').Replace('X', '≈').Replace('Y', '°').Replace('Z', '√');
						
						// TODO: Don't show message when everyone joins
						Map.Message("&2[ &f─ &6≥▼¼÷◘Æª&2 ]");
						Map.Message("%2Map: %a" + mapName + " %2made by %a" + mapAuthors);
						Map.Message("·");
						Map.Message("&7Search for chests to collect loot and kill your");
						Map.Message("&7opponents. Last player standing wins.");
						Map.Message("&a▓ " + Map.Config.Likes + " &c│ " + Map.Config.Dislikes);
						*/
						foreach (Player pl in players) {
							pl.Extras.Remove("NOG_INDEX");
						}
					}
				}
			}
		}
		
		// ============================================ ROUND =======================================
		int roundsOnThisMap = 1;
		
		protected override void DoRound() {
			if (!Running) return;
			DoRoundCountdown(30); // 30-second countdown to check if enough players before starting round
			if (!Running) return;
			
			UpdateMapConfig();
			if (!Running) return;
			
			List<Player> players = Map.getPlayers();
			//Vec3U16 coords = cfg.Spawn;
			//Position pos = Position.FromFeetBlockCoords(coords.X, coords.Y, coords.Z);
			
			foreach (Player pl in players) {
				Entities.UpdateEntityProp(pl, EntityProp.RotX, 0);
        		Entities.UpdateEntityProp(pl, EntityProp.RotZ, 0);
				Alive.Add(pl); // Adds them to the alive list
			}
			
			if (!Running) return;
			
			RoundInProgress = true;
				
			foreach (Player pl in players) {
				if (pl.level == Map) {
					pl.invincible = false;
					
					// I use this code for removing the spawn block for games like SkyWars where you spawn in a glass tube
					ushort x = (ushort) (pl.Pos.X / 32);
					ushort y = (ushort) (((pl.Pos.Y - Entities.CharacterHeight) / 32) - 1);
					ushort z = (ushort) (pl.Pos.Z / 32);
					
					// You need Goodlyay's /tempblock command to use this
					//Command.Find("TempBlock").Use(pl, "0 " + (ushort)x + " " + (ushort)y + " " + (ushort)z);
				}
			}
			
			UpdateAllStatus1();
			
			while (RoundInProgress && Alive.Count > 0) {
				Thread.Sleep(Interval);
				
				Level map = Map;
			}
		}

		void UpdatePlayersLeft() {
			if (!RoundInProgress) return;
			Player[] alive = Alive.Items;
			List<Player> players = Map.getPlayers();
			
			if (alive.Length == 1) { // Nobody left except winner
				Map.Message(alive[0].ColoredName + " %Sis the winner!");
				EndRound(alive[0]);
			} else { // Show alive player count
				Map.Message("%b" + alive.Length + " %Splayers left!");
			}
			UpdateAllStatus1();
		}
		
		public override void EndRound() { EndRound(null); }
		void EndRound(Player winner) {
			RoundInProgress = false;
			Alive.Clear();
			
			UpdateAllStatus1();
			if (winner != null) {
				winner.SendMessage("Congratulations, you won this round of NOG!");
				// TODO: Money
			}
			
			BufferedBlockSender bulk = new BufferedBlockSender(Map);
			
			bulk.Flush();
		}
		
		// ============================================ STATS =======================================
	}
	
	// This is the command the player will type. E.g, /skywars or /sw
	public sealed class CmdNameOfGamemode : RoundsGameCmd {
		public override string name { get { return "NameOfGamemode"; } }
		public override string shortcut { get { return "NOG"; } }
		protected override RoundsGame Game { get { return NOGGame.Instance; } }
		public override CommandPerm[] ExtraPerms {
			get { return new[] { new CommandPerm(LevelPermission.Operator, "can manage NameOfGamemode") }; }
		}
		
		protected override void HandleStart(Player p, RoundsGame game, string[] args) {
			if (game.Running) { p.Message("{0} is already running", game.GameName); return; }
			
			int interval = 150;
			if (args.Length > 1 && !CommandParser.GetInt(p, args[1], "Delay", ref interval, 1, 1000)) return;
			
			((NOGGame)game).Interval = interval;
			game.Start(p, "", int.MaxValue);
		}
		
		protected override void HandleSet(Player p, RoundsGame game, string[] args) {
			if (args.Length < 2) { Help(p, "set"); return; }
			string prop = args[1];
			
			if (prop.CaselessEq("spawn")) {
				NOGMapConfig cfg = RetrieveConfig(p);
				cfg.Spawn = (Vec3U16)p.Pos.FeetBlockCoords;
				p.Message("Set spawn pos to: &b{0}", cfg.Spawn);
				UpdateConfig(p, cfg);
				return;
			}
			
			if (args.Length < 3) { Help(p, "set"); }
		}
		
		static NOGMapConfig RetrieveConfig(Player p) {
			NOGMapConfig cfg = new NOGMapConfig();
			cfg.SetDefaults(p.level);
			cfg.Load(p.level.name);
			return cfg;
		}
		
		static void UpdateConfig(Player p, NOGMapConfig cfg) {
			if (!Directory.Exists("NameOfGamemode")) Directory.CreateDirectory("NameOfGamemode");
			cfg.Save(p.level.name);
			
			if (p.level == NOGGame.Instance.Map)
				NOGGame.Instance.UpdateMapConfig();
		}
		
		public override void Help(Player p, string message) {
			if (message.CaselessEq("h2p")) {
				p.Message("%H2-16 players will spawn. You will have 10 seconds grace");
				p.Message("%Hperiod in which you cannot be killed. After these");
				p.Message("%H10 seconds it's anyone's game. Click on chests to gain");
				p.Message("%Hloot and click on people to attack them.");
				p.Message("%HLast person standing wins the game.");
			} else {
				base.Help(p, message);
			}
		}
		
		public override void Help(Player p) {
			p.Message("%T/NOG start %H- Starts a game of NameOfGamemode");
			p.Message("%T/NOG stop %H- Immediately stops NameOfGamemode");
			p.Message("%T/NOG end %H- Ends current round of NameOfGamemode");
			p.Message("%T/NOG add/remove %H- Adds/removes current map from the map list");
			p.Message("%T/NOG status %H- Outputs current status of NameOfGamemode");
			p.Message("%T/NOG go %H- Moves you to the current NameOfGamemode map.");
		}
	}
}
