using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Facepunch.Extend;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using UnityEngine;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info("Easy Vote Extended", "dFxPhoeniX", "3.0.4")]
    [Description("The best Rust server voting system")]
    public class EasyVoteExtended : RustPlugin
    {
        private IEnumerator coroutine;

        ////////////////////////////////////////////////////////////
        // Oxide Hooks
        ////////////////////////////////////////////////////////////

        private void Init()
        {
            _config = Config.ReadObject<PluginConfig>();
            LoadMessages();
        }

        private void OnServerInitialized()
        {
            ConsoleLog("Easy Vote Extended has been initialized...");
        }

        private void Unload()
        {
            if (coroutine != null) ServerMgr.Instance.StopCoroutine(coroutine);
        }

        private void OnNewSave(string filename)
        {
            _Debug("------------------------------");
            _Debug("Method: OnNewSave");

            ConsoleLog("New map data detected!");

            if (_config.PluginSettings[ConfigDefaultKeys.ClearRewardsOnWipe] == "true")
            {
                _Debug("Wiping all votes from data file");
                ResetAllVoteData();
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: OnPlayerConnected");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            CheckIfPlayerDataExists(player);

            if (!_config.NotificationSettings[ConfigDefaultKeys.OnPlayerSleepEnded].ToBool() &&
                _config.NotificationSettings[ConfigDefaultKeys.OnPlayerConnected].ToBool())
            {
                CheckVotingStatus(player);
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: OnPlayerSleepEnded");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            if (_config.NotificationSettings[ConfigDefaultKeys.OnPlayerSleepEnded].ToBool())
            {
                CheckVotingStatus(player);
            }
        }

        ////////////////////////////////////////////////////////////
        // General Methods
        ////////////////////////////////////////////////////////////

        private void HandleClaimWebRequestCallback(int code, string response, BasePlayer player, string url, string serverName, string site)
        {
            if (code != 200)
            {
                ConsoleError($"An error occurred while trying to check the claim status of the player {player.displayName}:{player.UserIDString}");
                ConsoleWarn($"URL: {url}");
                ConsoleWarn($"HTTP Code: {code} | Response: {response} | Server Name: {serverName}");
                ConsoleWarn("This error could be due to a malformed or incorrect server token, id, or player id / username issue. Most likely its due to your server key being incorrect. Check that you server key is correct.");
                return;
            }
            
            _Debug("------------------------------");
            _Debug("Method: HandleClaimWebRequestCallback");
            _Debug($"Site: {site}");
            _Debug($"Code: {code}");
            _Debug($"Response: {response}");
            _Debug($"URL: {url}");
            _Debug($"ServerName: {serverName}");
            _Debug($"Player Name: {player.displayName}");
            _Debug($"Player SteamID: {player.UserIDString}");
            _Debug("Web Request Type: Claim");

            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                _Debug($"Verbose Debug Enabled, Setting Response Code to: {_config.DebugSettings[ConfigDefaultKeys.ClaimAPIRepsonseCode]}");
                response = _config.DebugSettings[ConfigDefaultKeys.ClaimAPIRepsonseCode];
            }

            if (response == "1")
            {
                HandleVoteCount(player);
                player.ChatMessage(_lang("ThankYou", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], DataFile[player.UserIDString].ToString(), site));

                if (_config.Discord[ConfigDefaultKeys.DiscordEnabled].ToBool())
                {
                    coroutine = DiscordSendMessage(_lang("DiscordWebhookMessage", player.UserIDString, player.displayName, serverName, site));
                    ServerMgr.Instance.StartCoroutine(coroutine);
                }

                if (_config.NotificationSettings[ConfigDefaultKeys.GlobalChatAnnouncements] == "true")
                {
                    foreach (var p in BasePlayer.activePlayerList)
                    {
                        p.ChatMessage(_lang("GlobalChatAnnouncements", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix],player.displayName, DataFile[player.UserIDString].ToString()));
                    }
                }
            }
            else if (response == "2")
            {
                player.ChatMessage(_lang("AlreadyVoted", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site));
            }
            else
            {
                player.ChatMessage(_lang("ClaimStatus", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site, serverName));
            }
        }
        
        private void HandleStatusWebRequestCallback(int code, string response, BasePlayer player, string url, string serverName, string site)
        {
            if (code != 200)
            {
                ConsoleError($"An error occurred while trying to check the claim status of the player {player.displayName}:{player.UserIDString}");
                ConsoleWarn($"URL: {url}");
                ConsoleWarn($"HTTP Code: {code} | Response: {response} | Server Name: {serverName}");
                ConsoleWarn("This error could be due to a malformed or incorrect server token, id, or player id / username issue. Most likely its due to your server key being incorrect. Check that you server key is correct.");
                return;
            }

            _Debug("------------------------------");
            _Debug("Method: HandleClaimWebRequestCallback");
            _Debug($"Site: {site}");
            _Debug($"Code: {code}");
            _Debug($"Response: {response}");
            _Debug($"URL: {url}");
            _Debug($"ServerName: {serverName}");
            _Debug($"Player Name: {player.displayName}");
            _Debug($"Player SteamID: {player.UserIDString}");
            _Debug($"Web Request Type: Status/Check");

            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                _Debug($"Verbose Debug Enabled, Setting Response Code to: {_config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode]}");
                response = _config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode];
            }

            if (response == "0")
            {
                player.ChatMessage(_lang("NoRewards", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], serverName, site));
            }
            else if (response == "1")
            {
                player.ChatMessage(_lang("RememberClaim", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site));
            }
            else if (response == "2")
            {
                player.ChatMessage(_lang("AlreadyVoted", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site));
            }
        }

        private void HandleVoteCount(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: HandleVoteCount");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            int playerVoteCount = (int) DataFile[player.UserIDString];
            _Debug($"Current VoteCount: {playerVoteCount}");

            playerVoteCount += 1;
            DataFile[player.UserIDString] = playerVoteCount;
            SaveDataFile(DataFile);
            _Debug($"Updated Vote Count: {playerVoteCount}");

            if (_config.PluginSettings[ConfigDefaultKeys.RewardIsCumulative] == "true")
            {
                GiveCumulativeRewards(player, (int) DataFile[player.UserIDString]);
            }
            else
            {
                GiveNormalRewards(player, (int) DataFile[player.UserIDString]);
            }
        }

        private void GiveCumulativeRewards(BasePlayer player, int playerVoteCount)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveCumulativeRewards");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            GiveEveryReward(player);

            GiveFirstReward(player);

            foreach (KeyValuePair<string, List<string>> rewards in _config.Rewards)
            {
                if (rewards.Key.ToInt() <= playerVoteCount)
                {
                    GiveSubsequentReward(player, rewards.Value);
                }
            }
        }

        private void GiveNormalRewards(BasePlayer player, int playerVoteCount)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveNormalRewards");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            GiveEveryReward(player);

            if (playerVoteCount == 1)
            {
                GiveFirstReward(player);
            }

            foreach (KeyValuePair<string, List<string>> rewards in _config.Rewards)
            {
                if (rewards.Key.ToInt() == playerVoteCount)
                {
                    GiveSubsequentReward(player, rewards.Value);
                }
            }
        }

        private void GiveEveryReward(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveEveryReward");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            foreach (string rewardCommand in _config.Rewards["@"])
            {
                string command = ParseRewardCommand(player, rewardCommand);
                _Debug($"Reward Command: {command}");
                rust.RunServerCommand(command);
            }
        }

        private void GiveFirstReward(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveFirstReward");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            foreach (string rewardCommand in _config.Rewards["first"])
            {
                string command = ParseRewardCommand(player, rewardCommand);
                _Debug($"Reward Command: {command}");
                rust.RunServerCommand(command);
            }
        }

        private void GiveSubsequentReward(BasePlayer player, List<string> rewardsList)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveSubsequentReward");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");
            _Debug($"Vote Count: {DataFile[player.UserIDString]}");

            foreach (string rewardCommand in rewardsList)
            {
                string command = ParseRewardCommand(player, rewardCommand);
                _Debug($"Reward Command: {command}");
                rust.RunServerCommand(command);
            }
        }

        private string ParseRewardCommand(BasePlayer player, string command)
        {
            return command
                .Replace("{playerid}", player.UserIDString)
                .Replace("{playername}", player.displayName);
        }

        private void CheckIfPlayerDataExists(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: CheckIfPlayerDataExists");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            if (DataFile[player.UserIDString] == null)
            {
                _Debug($"{player.displayName} data does not exist. Creating new entry now.");

                DataFile[player.UserIDString] = 0;
                SaveDataFile(DataFile);

                _Debug($"{player.displayName} Data has been created.");
            }
        }

        private void ResetAllVoteData()
        {
            _Debug("------------------------------");
            _Debug("Method: ResetAllVoteData");

            foreach (KeyValuePair<string, object> player in DataFile.ToList())
            {
                DataFile[player.Key] = 0;
                _Debug($"Player {player.Key} vote count reset...");
            }

            SaveDataFile(DataFile);
        }

        private void CheckVotingStatus(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: CheckVotingStatus");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            var timeout = 5000;
            if (_config.NotificationSettings[ConfigDefaultKeys.PleaseWaitMessage].ToBool())
            {
                player.ChatMessage(_lang("PleaseWait", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> ServersKVP in _config.Servers)
            {
                _Debug($"ServersKVP.Key: {ServersKVP.Key.ToString()}");

                foreach (KeyValuePair<string, string> IDKeys in ServersKVP.Value)
                {
                    _Debug($"IDKeys.Key: {IDKeys.Key.ToString().ToLower()}");
                    _Debug($"IDKeys.Value: {IDKeys.Value.ToString()}");

                    if (!_config.VoteSitesAPI.ContainsKey(IDKeys.Key))
                    {
                        ConsoleWarn($"The voting website {IDKeys.Key} does not exist in the API section of the config!");
                        continue;
                    }

                    var APILink = _config.VoteSitesAPI[IDKeys.Key.ToString()][ConfigDefaultKeys.apiStatus];
                    _Debug($"Check Status API Link: {APILink.ToString()}");
                    var usernameAPIEnabled = _config.VoteSitesAPI[IDKeys.Key.ToString()][ConfigDefaultKeys.apiUsername];
                    _Debug($"API Username Enabled: {usernameAPIEnabled}");
                    string[] IDKey = IDKeys.Value.Split(':');
                    _Debug($"ID: {IDKey[0]}");
                    _Debug($"Key/Token: {IDKey[1]}");

                    string formattedURL = "";
                    if (usernameAPIEnabled.ToBool())
                    {
                        formattedURL = string.Format(APILink, IDKey[1], player.displayName);
                    }
                    else if (IDKeys.Key.ToString().ToLower() == "rustservers.gg")
                    {
                        formattedURL = string.Format(APILink, IDKey[1], player.UserIDString, IDKey[0]);
                    }
                    else
                    {
                        formattedURL = string.Format(APILink, IDKey[1], player.UserIDString);
                    }

                    _Debug($"Formatted URL: {formattedURL}");

                    webrequest.Enqueue(formattedURL, null,
                        (code, response) => HandleStatusWebRequestCallback(code, response, player, formattedURL, ServersKVP.Key.ToString(), IDKeys.Key.ToString()), this,
                        RequestMethod.GET, null, timeout);

                    _Debug("------------------------------");
                }
            }
        }

        protected void ConsoleLog(object message)
        {
            Puts(message?.ToString());
        }

        protected void ConsoleError(string message)
        {
            if (Convert.ToBoolean(_config.PluginSettings[ConfigDefaultKeys.LogEnabled]))
                LogToFile("EasyVoteExtended", $"ERROR: {message}", this);

            Debug.LogError($"ERROR: " + message);
        }

        protected void ConsoleWarn(string message)
        {
            if (Convert.ToBoolean(_config.PluginSettings[ConfigDefaultKeys.LogEnabled]))
                LogToFile("EasyVoteExtended", $"WARNING: {message}", this);

            Debug.LogWarning($"WARNING: " + message);
        }

        protected void _Debug(string message, string arg = null)
        {
            if (_config.DebugSettings[ConfigDefaultKeys.DebugEnabled] == "true")
            {
                if (Convert.ToBoolean(_config.PluginSettings[ConfigDefaultKeys.LogEnabled]))
                    LogToFile("EasyVoteExtended", $"DEBUG: {message}", this);

                Puts($"DEBUG: {message}");

                if (arg != null)
                {
                    Puts($"DEBUG ARG: {arg}");
                }
            }
        }

        private IEnumerator DiscordSendMessage(string msg)
        {
            if (_config.Discord[ConfigDefaultKeys.discordWebhookURL] != "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks" || !string.IsNullOrEmpty(_config.Discord[ConfigDefaultKeys.discordWebhookURL]))
            {
                WWWForm formData = new WWWForm();
                string content = $"{msg}\n";
                formData.AddField("content", content);

                using (var request = UnityWebRequest.Post(_config.Discord[ConfigDefaultKeys.discordWebhookURL], formData))
                {
                    yield return request.SendWebRequest();
                    if ((request.isNetworkError || request.isHttpError) && request.error.Contains("Too Many Requests"))
                    {
                        Puts("Discord Webhook Rate Limit Exceeded... Waiting 30 seconds...");
                        yield return new WaitForSeconds(30f);
                    }
                }
            }

            ServerMgr.Instance.StopCoroutine(coroutine);
        }

        ////////////////////////////////////////////////////////////
        // Commands
        ////////////////////////////////////////////////////////////

        [ChatCommand("rewardlist")]
        private void RewardListChatCommand(BasePlayer player, string command, string[] args)
        {
            player.ChatMessage(_lang("RewardsList", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));

            foreach (KeyValuePair<string, string> kvp in _config.RewardDescriptions)
            {
                if (kvp.Key == "@")
                {
                    player.ChatMessage(_lang("EveryVote", player.UserIDString, kvp.Value));
                }
                else if (kvp.Key == "first")
                {
                    player.ChatMessage(_lang("FirstVote", player.UserIDString, kvp.Value));
                }
                else
                {
                    player.ChatMessage(_lang("NumberVote", player.UserIDString, kvp.Key, kvp.Value));
                }
            }
        }

        [ChatCommand("vote")]
        private void VoteChatCommand(BasePlayer player, string command, string[] args)
        {
            player.ChatMessage(_lang("VoteList", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));

            HashSet<string> displayedServers = new HashSet<string>();

            foreach (KeyValuePair<string, Dictionary<string, string>> kvp in _config.VoteSitesAPI)
            {
                foreach (KeyValuePair<string, Dictionary<string, string>> serverskvp in _config.Servers)
                {
                    if (displayedServers.Contains(serverskvp.Key))
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, string> serversvcl in _config.ServersCustomLink)
                    {
                        if (Equals(serverskvp.Key, serversvcl.Key))
                        {
                            player.ChatMessage(_lang("VoteLinkCustom", player.UserIDString, serverskvp.Key, string.Format(serversvcl.Value)));
                            displayedServers.Add(serverskvp.Key);
                            break;
                        }
                    }

                    if (displayedServers.Contains(serverskvp.Key)) 
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, string> serveridkeys in serverskvp.Value)
                    {
                        if (Equals(kvp.Key, serveridkeys.Key))
                        {
                            string[] parts = serveridkeys.Value.Split(':');
                            player.ChatMessage(_lang("VoteLink", player.UserIDString, serverskvp.Key, kvp.Key, string.Format(kvp.Value[ConfigDefaultKeys.apiLink], parts[0])));
                        }
                    }
                }
            }

            player.ChatMessage(_lang("EarnReward", player.UserIDString));
        }

        [ChatCommand("claim")]
        private void ClaimChatCommand(BasePlayer player, string command, string[] args)
        {
            CheckIfPlayerDataExists(player);

            _Debug("------------------------------");
            _Debug("Method: ClaimChatCommand");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            var timeout = 5000;
            if (_config.NotificationSettings[ConfigDefaultKeys.PleaseWaitMessage].ToBool())
            {
                player.ChatMessage(_lang("PleaseWait", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> ServersKVP in _config.Servers)
            {
                _Debug($"ServersKVP.Key: {ServersKVP.Key.ToString()}");

                foreach (KeyValuePair<string, string> IDKeys in ServersKVP.Value)
                {
                    _Debug($"IDKeys.Key: {IDKeys.Key.ToString().ToLower()}");
                    _Debug($"IDKeys.Value: {IDKeys.Value.ToString()}");

                    if (!_config.VoteSitesAPI.ContainsKey(IDKeys.Key))
                    {
                        ConsoleWarn($"The voting website {IDKeys.Key} does not exist in the API section of the config!");
                        continue;
                    }

                    var APILink = _config.VoteSitesAPI[IDKeys.Key.ToString()][ConfigDefaultKeys.apiClaim];
                    _Debug($"Check Status API Link: {APILink.ToString()}");
                    var usernameAPIEnabled = _config.VoteSitesAPI[IDKeys.Key.ToString()][ConfigDefaultKeys.apiUsername];
                    _Debug($"API Username Enabled: {usernameAPIEnabled}");
                    string[] IDKey = IDKeys.Value.Split(':');
                    _Debug($"ID: {IDKey[0]}");
                    _Debug($"Key/Token: {IDKey[1]}");

                    string formattedURL = "";
                    if (usernameAPIEnabled.ToBool())
                    {
                        formattedURL = string.Format(APILink, IDKey[1], player.displayName);
                    }
                    else if (IDKeys.Key.ToString().ToLower() == "rustservers.gg")
                    {
                        formattedURL = string.Format(APILink, IDKey[1], player.UserIDString, IDKey[0]);
                    }
                    else
                    {
                        formattedURL = string.Format(APILink, IDKey[1], player.UserIDString);
                    }
                    _Debug($"Formatted URL: {formattedURL}");

                    webrequest.Enqueue(formattedURL, null,
                        (code, response) => HandleClaimWebRequestCallback(code, response, player, formattedURL, ServersKVP.Key.ToString(), IDKeys.Key.ToString()), this,
                        RequestMethod.GET, null, timeout);

                    _Debug("------------------------------");
                }
            }

            timer.Once(5f, () =>
            {
                player.ChatMessage(_lang("ClaimReward", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
            });
        }

        [ConsoleCommand("eve.clearvote")]
        private void ClearPlayerVoteCountConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!arg.HasArgs(1))
            {
                ConsoleError("Command clearvote usage: eve.clearvote steamid|username");
                return;
            }

            BasePlayer player = arg.GetPlayer(0);
            if (player == null)
            {
                ConsoleError($"Failed to find player with ID/Username/IP of: {arg.GetString(0)}");
                return;
            }

            DataFile[player.UserIDString] = 0;
            SaveDataFile(DataFile);
            ConsoleLog($"{player.displayName}/{player.UserIDString} vote count has been reset to 0");
        }

        [ConsoleCommand("eve.checkvote")]
        private void CheckPlayerVoteCountConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!arg.HasArgs(1))
            {
                ConsoleError("Command checkvote usage: eve.checkvote steamid|username");
                return;
            }

            BasePlayer player = arg.GetPlayer(0);
            if (player == null)
            {
                ConsoleError($"Failed to find player with ID/Username/IP of: {arg.GetString(0)}");
                return;
            }

            ConsoleLog($"Player {player.displayName}/{player.UserIDString} has {getPlayerVotes(player.UserIDString)} votes total");
        }

        [ConsoleCommand("eve.setvote")]
        private void SetPlayerVoteCountConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!arg.HasArgs(2))
            {
                ConsoleError("Command setvote usage: eve.setvote steamid|username numberOfVotes");
                return;
            }

            BasePlayer player = arg.GetPlayer(0);
            if (player == null)
            {
                ConsoleError($"Failed to find player with ID/Username/IP of: {arg.GetString(0)}");
                return;
            }

            DataFile[player.UserIDString] = arg.GetInt(1);
            SaveDataFile(DataFile);
            ConsoleLog($"Player {player.displayName}/{player.UserIDString} vote count has been updated to {arg.GetString(1)}");
        }

        [ConsoleCommand("eve.resetvotedata")]
        private void ResetAllVoteDataConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.HasArgs(1))
            {
                ConsoleError("Command eve.resetvotedata usage: This command has no arguments. Run the command again with no arguments");
                return;
            }

            ResetAllVoteData();
        }

        ////////////////////////////////////////////////////////////
        // Configs
        ////////////////////////////////////////////////////////////
        
        private PluginConfig _config;

        protected override void SaveConfig() => Config.WriteObject(_config);

        private string _lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        private class ConfigDefaultKeys
        {
            public const string apiClaim = "API Claim Reward (GET URL)";
            public const string apiStatus = "API Vote status (GET URL)";
            public const string apiLink = "Vote link (URL)";
            public const string apiUsername = "Site Uses Username Instead of Player Steam ID?";

            public const string discordTitle = "Discord Title";
            public const string discordWebhookURL = "Discord webhook (URL)";
            public const string DiscordEnabled = "DiscordMessage Enabled (true / false)";

            public const string Prefix = "Chat Prefix";
            public const string LogEnabled = "Enable logging => logs/EasyVoteExtended (true / false)";
            public const string RewardIsCumulative = "Vote rewards cumulative (true / false)";
            public const string ClearRewardsOnWipe = "Wipe Rewards Count on Map Wipe?";

            public const string GlobalChatAnnouncements = "Globally announcment in chat when player voted (true / false)";
            public const string PleaseWaitMessage = "Enable the 'Please Wait' message when checking voting status?";
            public const string OnPlayerSleepEnded = "Notify player of rewards when they stop sleeping?";
            public const string OnPlayerConnected = "Notify player of rewards when they connect to the server?";

            public const string DebugEnabled = "Debug Enabled?";
            public const string VerboseDebugEnabled = "Enable Verbose Debugging?";
            public const string CheckAPIResponseCode = "Set Check API Response Code (0 = Not found, 1 = Has voted and not claimed, 2 = Has voted and claimed)";
            public const string ClaimAPIRepsonseCode = "Set Claim API Response Code (0 = Not found, 1 = Has voted and not claimed. The vote will now be set as claimed., 2 = Has voted and claimed";
        }
        
        private class PluginConfig
        {
            [JsonProperty(PropertyName = "Debug Settings")]
            public Dictionary<string, string> DebugSettings;
            
            [JsonProperty(PropertyName = "Plugin Settings")]
            public Dictionary<string, string> PluginSettings;
            
            [JsonProperty(PropertyName = "Notification Settings")]
            public Dictionary<string, string> NotificationSettings;

            [JsonProperty(PropertyName = "Discord")]
            public Dictionary<string, string> Discord;

            [JsonProperty(PropertyName = "Rewards")]
            public Dictionary<string, List<string>> Rewards;

            [JsonProperty(PropertyName = "Reward Descriptions")]
            public Dictionary<string, string> RewardDescriptions;

            [JsonProperty(PropertyName = "Server Voting IDs and Keys")]
            public Dictionary<string, Dictionary<string, string>> Servers;

            [JsonProperty(PropertyName = "Server Vote Custom link")]
            public Dictionary<string, string> ServersCustomLink;
            
            [JsonProperty(PropertyName = "Voting Sites API Information")]
            public Dictionary<string, Dictionary<string, string>> VoteSitesAPI;
            
        }

        protected override void LoadDefaultConfig()
        {
            
            _config = new PluginConfig();
            _config.DebugSettings = new Dictionary<string, string>
            {
                {ConfigDefaultKeys.DebugEnabled, "false"},
                {ConfigDefaultKeys.VerboseDebugEnabled, "false"},
                {ConfigDefaultKeys.CheckAPIResponseCode, "0"},
                {ConfigDefaultKeys.ClaimAPIRepsonseCode, "0"}
            };
            _config.PluginSettings = new Dictionary<string, string>
            {
                {ConfigDefaultKeys.LogEnabled, "true"},
                {ConfigDefaultKeys.ClearRewardsOnWipe, "false"},
                {ConfigDefaultKeys.RewardIsCumulative, "false"},
                {ConfigDefaultKeys.Prefix, "<color=#e67e22>[EasyVote]</color> "},
            };
            _config.NotificationSettings = new Dictionary<string, string>
            {
                {ConfigDefaultKeys.GlobalChatAnnouncements, "true"},
                {ConfigDefaultKeys.PleaseWaitMessage, "true"},
                {ConfigDefaultKeys.OnPlayerSleepEnded, "false"},
                {ConfigDefaultKeys.OnPlayerConnected, "true"}
            };
            _config.Discord = new Dictionary<string, string>
            {
                {ConfigDefaultKeys.discordWebhookURL, "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks"},
                {ConfigDefaultKeys.DiscordEnabled, "false"},
                {ConfigDefaultKeys.discordTitle, "A player has just voted for us!"} 
            };
            _config.Rewards = new Dictionary<string, List<string>>
            {
                { "@", new List<string>() { "giveto {playerid} supply.signal 1" } },
                { "first", new List<string>() { "giveto {playerid} stones 10000", "sr add {playerid} 10000" } },
                { "3", new List<string>() { "addgroup {playerid} vip 7d" } },
                { "6", new List<string>() { "grantperm {playerid} plugin.test 1d" } },
                { "10", new List<string>() { "zl.lvl {playerid} * 2" } }
            };
            _config.RewardDescriptions = new Dictionary<string, string>
            {
                { "@", "1 Supply Signal" },
                { "first", "10000 Stones, 10000 RP" },
                { "3", "7 days of VIP rank" },
                { "6", "1 day of plugin.test permission" },
                { "10", "2 zLevels in Every Category" }
            };
            _config.Servers = new Dictionary<string, Dictionary<string, string>>
            {
                { "ServerName1", new Dictionary<string, string>() { { "Rust-Servers.net", "ID:KEY" }, { "Rustservers.gg", "ID:KEY" }, { "BestServers.com", "ID:KEY" }, { "GamesFinder.net", "ID:KEY" }, { "Top-Games.net", "ID:KEY" }, { "TrackyServer.com", "ID:KEY" } } },
                { "ServerName2", new Dictionary<string, string>() { { "Rust-Servers.net", "ID:KEY" }, { "Rustservers.gg", "ID:KEY" }, { "BestServers.com", "ID:KEY" }, { "GamesFinder.net", "ID:KEY" }, { "Top-Games.net", "ID:KEY" }, { "TrackyServer.com", "ID:KEY" } } }
            };
            _config.ServersCustomLink = new Dictionary<string, string>
            {
                { "ServerName1", "https://vote.servername1.com" }
            };
            _config.VoteSitesAPI = new Dictionary<string, Dictionary<string, string>>
            {
                {
                    "Rust-Servers.net",
                    new Dictionary<string, string>()
                    {
                        {
                            ConfigDefaultKeys.apiClaim,
                            "https://rust-servers.net/api/?action=custom&object=plugin&element=reward&key={0}&steamid={1}"
                        },
                        {
                            ConfigDefaultKeys.apiStatus,
                            "https://rust-servers.net/api/?object=votes&element=claim&key={0}&steamid={1}"
                        },
                        { ConfigDefaultKeys.apiLink, "https://rust-servers.net/server/{0}" },
                        { ConfigDefaultKeys.apiUsername, "false"}
                    }
                },
                {
                    "Rustservers.gg",
                    new Dictionary<string, string>()
                    {
                        {
                            ConfigDefaultKeys.apiClaim,
                            "https://rustservers.gg/vote-api.php?action=claim&key={0}&server={2}&steamid={1}"
                        },
                        {
                            ConfigDefaultKeys.apiStatus,
                            "https://rustservers.gg/vote-api.php?action=status&key={0}&server={2}&steamid={1}"
                        },
                        { ConfigDefaultKeys.apiLink, "https://rustservers.gg/server/{0}" },
                        { ConfigDefaultKeys.apiUsername, "false"}
                    }
                },
                {
                    "BestServers.com",
                    new Dictionary<string, string>()
                    {
                        {
                            ConfigDefaultKeys.apiClaim,
                            "https://bestservers.com/api/vote.php?action=claim&key={0}&steamid={1}"
                        },
                        {
                            ConfigDefaultKeys.apiStatus,
                            "https://bestservers.com/api/vote.php?action=status&key={0}&steamid={1}"
                        },
                        { ConfigDefaultKeys.apiLink, "https://bestservers.com/server/{0}" },
                        { ConfigDefaultKeys.apiUsername, "false"}
                    }
                },
                {
                    "GamesFinder.net",
                    new Dictionary<string, string>()
                    {
                        {
                            ConfigDefaultKeys.apiClaim,
                            "https://www.gamesfinder.net/api/vote?mode=claim&key={0}&steamid={1}"
                        },
                        {
                            ConfigDefaultKeys.apiStatus,
                            "https://www.gamesfinder.net/api/vote?key={0}&steamid={1}"
                        },
                        { ConfigDefaultKeys.apiLink, "https://www.gamesfinder.net/server/{0}" },
                        { ConfigDefaultKeys.apiUsername, "false"}
                    }
                },
                {
                    "Top-Games.net",
                    new Dictionary<string, string>()
                    {
                        {
                            ConfigDefaultKeys.apiClaim,
                            "https://api.top-games.net/v1/votes/claim-username?server_token={0}&playername={1}"
                        },
                        {
                            ConfigDefaultKeys.apiStatus,
                            "https://api.top-games.net/v1/votes/check?server_token={0}&playername={1}"
                        },
                        { ConfigDefaultKeys.apiLink, "https://top-games.net/rust/{0}" },
                        { ConfigDefaultKeys.apiUsername, "false"}
                    }
                },
                {
                    "TrackyServer.com",
                    new Dictionary<string, string>()
                    {
                        {
                            ConfigDefaultKeys.apiClaim,
                            "https://api.trackyserver.com/vote/?action=claim&key={0}&steamid={1}"
                        },
                        {
                            ConfigDefaultKeys.apiStatus,
                            "https://api.trackyserver.com/vote/?action=status&key={0}&steamid={1}"
                        },
                        { ConfigDefaultKeys.apiLink, "https://trackyserver.com/server/{0}" },
                        { ConfigDefaultKeys.apiUsername, "false"}
                    }
                }
            };

            SaveConfig();
            ConsoleWarn("A new configuration file has been generated!");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                {
                    LoadDefaultConfig();
                    // SaveConfig();
                }
            }
            catch
            {
                ConsoleError("The configuration file is corrupted. Please delete the config file and reload the plugin.");
                LoadDefaultConfig();
                SaveConfig();
            }
        }

        private void LoadMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to use this command!",
                ["ClaimStatus"] = "{0} <color=#e67e22>{1}</color> reports you have not voted yet on <color=#e67e22>{2}</color>. Vote now!",
                ["ClaimReward"] = "{0} If you voted, and the votes went through, then you just received your vote reward(s). Enjoy!",
                ["PleaseWait"] = "{0} Checking all the vote sites API's... Please be patient as this can take some time...",
                ["VoteList"] = "{0} You can vote for our server at the following links:",
                ["EarnReward"] = "When you have voted, type <color=#e67e22>/claim</color> to claim your reward(s)!",
                ["ThankYou"] = "{0} Thank you for voting! You have voted <color=#e67e22>{1}</color> time(s) Here is your reward for: <color=#e67e22>{2}</color>",
                ["NoRewards"] = "{0} You haven't voted for <color=#e67e22>{1}</color> on <color=#e67e22>{2}</color> yet! Type <color=#e67e22>/vote</color> to get started!",
                ["RememberClaim"] = "{0} <color=#e67e22>{1}</color> is reporting that you have an unclaimed reward! Use <color=#e67e22>/claim</color> to claim your reward!\n You have to claim your reward within <color=#e67e22>24h</color>! Otherwise it will be gone!",
                ["GlobalChatAnnouncements"] = "{0} <color=#e67e22>{1}</color> has voted <color=#e67e22>{2}</color> time(s) and just received their rewards. Find out where you can vote by typing <color=#e67e22>/vote</color>\nTo see a list of available rewards type <color=#e67e22>/rewardlist</color>",
                ["AlreadyVoted"] = "{0} <color=#e67e22>{1}</color> reports you have already voted! Vote again later.",
                ["DiscordWebhookMessage"] = "{0} has voted for {1} on {2} and got some rewards! Type /rewardlist in game to find out what you can get when you vote for us!",
                ["RewardsList"] = "{0} The following rewards are given for voting!",
                ["EveryVote"] = "Every Vote: <color=#e67e22>{0}</color>",
                ["FirstVote"] = "First Vote: <color=#e67e22>{0}</color>",
                ["NumberVote"] = "Vote no. {0}: <color=#e67e22>{1}</color>",
                ["VoteLink"] = "{0} ({1}): <color=#e67e22>{2}</color>",
                ["VoteLinkCustom"] = "{0}: <color=#e67e22>{1}</color>"
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "Nu ai permisiunea să folosești această comandă!",
                ["ClaimStatus"] = "{0} <color=#e67e22>{1}</color> raportează că nu ai votat încă pe <color=#e67e22>{2}</color>. Votează acum!",
                ["ClaimReward"] = "{0} Dacă ai votat și voturile au fost înregistrate, atunci tocmai ai primit recompensa ta pentru vot(uri). Bucură-te de ele!",
                ["PleaseWait"] = "{0} Se verifică toate API-urile site-urilor de vot... Te rugăm să ai răbdare, acest proces poate dura ceva timp...",
                ["VoteList"] = "{0} Poți vota pentru server-ul nostru accesând următoarele link-uri:",
                ["EarnReward"] = "După ce ai votat, scrie <color=#e67e22>/claim</color> pentru a revendica recompensa ta!",
                ["ThankYou"] = "{0} Mulțumim pentru vot! Ai votat de <color=#e67e22>{1}</color> ori. Iată recompensa ta pentru asta: <color=#e67e22>{2}</color>",
                ["NoRewards"] = "{0} Nu ai votat pentru <color=#e67e22>{1}</color> pe <color=#e67e22>{2}</color> încă! Scrie <color=#e67e22>/vote</color> pentru a începe!",
                ["RememberClaim"] = "{0} <color=#e67e22>{1}</color> raportează că ai o recompensă revendicată! Folosește <color=#e67e22>/claim</color> pentru a o revendica!\nTrebuie să revendici recompensa ta în termen de <color=#e67e22>24h</color>! Altfel, va fi pierdută!",
                ["GlobalChatAnnouncements"] = "{0} <color=#e67e22>{1}</color> a votat de <color=#e67e22>{2}</color> ori și tocmai a primit recompensele. Află unde poți vota scriind <color=#e67e22>/vote</color>\nPentru a vedea lista de recompense disponibile, scrie <color=#e67e22>/rewardlist</color>",
                ["AlreadyVoted"] = "{0} <color=#e67e22>{1}</color> raportează că ai votat deja! Poți vota din nou mai târziu.",
                ["DiscordWebhookMessage"] = "{0} a votat pentru {1} pe {2} și a primit recompense! Scrie /rewardlist în joc pentru a vedea ce poți obține când votezi pentru noi!",
                ["RewardsList"] = "Următoarele recompense sunt acordate pentru vot!",
                ["EveryVote"] = "Fiecare Vot: <color=#e67e22>{0}</color>",
                ["FirstVote"] = "Primul Vot: <color=#e67e22>{0}</color>",
                ["NumberVote"] = "Votul nr. {0}: <color=#e67e22>{1}</color>",
                ["VoteLink"] = "{0} ({1}): <color=#e67e22>{2}</color>",
                ["VoteLinkCustom"] = "{0}: <color=#e67e22>{1}</color>"
            }, this, "ro");
        }

        ////////////////////////////////////////////////////////////
        // Files
        ////////////////////////////////////////////////////////////

        protected internal static DynamicConfigFile DataFile = Interface.Oxide.DataFileSystem.GetDatafile("EasyVoteExtended");

        private void SaveDataFile(DynamicConfigFile data)
        {
            data.Save();
            _Debug("Data file has been updated.");
        }

        ////////////////////////////////////////////////////////////
        // Plugin Hooks
        ////////////////////////////////////////////////////////////

        [HookMethod(nameof(getPlayerVotes))]
        public int getPlayerVotes(string steamID)
        {
            if (DataFile[steamID] == null)
            {
                _Debug("getPlayerVotes(): Player data doesn't exist");
                return 0;
            }

            return (int) DataFile[steamID];
        }
    }
}