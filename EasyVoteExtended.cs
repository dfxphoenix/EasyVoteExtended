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
    [Info("Easy Vote Extended", "dFxPhoeniX&TimRS", "3.2.1")]
    [Description("The best Rust server voting system")]
    public class EasyVoteExtended : RustPlugin
    {
        private readonly HashSet<string> pendingClaims = new HashSet<string>();
        private readonly HashSet<string> pendingStatusChecks = new HashSet<string>();
        private readonly HashSet<ulong> scheduledVoteChecks = new HashSet<ulong>();
        private readonly HashSet<string> reportedConfigurationWarnings = new HashSet<string>();
        private readonly Queue<Action> claimRequestQueue = new Queue<Action>();
        private readonly Queue<Action> voteRequestQueue = new Queue<Action>();
        private readonly Dictionary<string, float> commandCooldowns = new Dictionary<string, float>();
        private Dictionary<string, List<string>> pendingRewardCommands = new Dictionary<string, List<string>>();

        private const int CurrentConfigurationVersion = 1;
        private const int DefaultAutomaticVoteCheckInterval = 300;
        private const int DefaultVoteFollowUpCheckDelay = 60;
        private const float VoteRequestSpacing = 0.2f;
        private const float ManualCommandCooldown = 10f;
        private const string PendingRewardsDataFileName = "EasyVoteExtended_PendingRewards";

        ////////////////////////////////////////////////////////////
        // Oxide Hooks
        ////////////////////////////////////////////////////////////

        private void Init()
        {
            if (_config == null)
            {
                _config = Config.ReadObject<PluginConfig>();
            }

            EnsureConfigDefaults();
            LoadMessages();
            LoadPendingRewardsData();
        }

        private void OnServerInitialized()
        {
            ConsoleLog("Easy Vote Extended has been initialized...");

            StartRequestQueueProcessor();
            StartAutomaticVoteChecks();

            timer.Once(2f, () =>
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList.ToArray())
                {
                    if (player == null || !player.IsConnected)
                    {
                        continue;
                    }

                    CheckIfPlayerDataExists(player);
                    DeliverPendingRewards(player);
                    CheckVotingStatus(player, false);
                }
            });
        }

        private void Unload()
        {
            pendingClaims.Clear();
            pendingStatusChecks.Clear();
            scheduledVoteChecks.Clear();
            reportedConfigurationWarnings.Clear();
            claimRequestQueue.Clear();
            voteRequestQueue.Clear();
            commandCooldowns.Clear();
            SavePendingRewardsData();
        }

        private void OnNewSave(string filename)
        {
            _Debug("------------------------------");
            _Debug("Method: OnNewSave");

            ConsoleLog("New map data detected!");

            if (_config.PluginSettings[ConfigDefaultKeys.ClearRewardsOnWipe].ToBool())
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

            timer.Once(2f, () =>
            {
                if (player != null && player.IsConnected)
                {
                    DeliverPendingRewards(player);
                }
            });

            bool checkOnSleepEnded = _config.NotificationSettings[ConfigDefaultKeys.OnPlayerSleepEnded].ToBool();
            bool notifyOnConnected = _config.NotificationSettings[ConfigDefaultKeys.OnPlayerConnected].ToBool();

            if (!checkOnSleepEnded)
            {
                CheckVotingStatus(player, notifyOnConnected);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
            {
                return;
            }

            commandCooldowns.Remove($"{player.UserIDString}:vote");
            commandCooldowns.Remove($"{player.UserIDString}:claim");
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: OnPlayerSleepEnded");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            DeliverPendingRewards(player);

            if (_config.NotificationSettings[ConfigDefaultKeys.OnPlayerSleepEnded].ToBool())
            {
                CheckVotingStatus(player);
            }
        }

        ////////////////////////////////////////////////////////////
        // General Methods
        ////////////////////////////////////////////////////////////

        private void HandleClaimWebRequestCallback(int code, string response, BasePlayer player, string url, string serverName, string site, bool notifyPlayer, string pendingClaimKey)
        {
            pendingClaims.Remove(pendingClaimKey);

            if (code != 200)
            {
                ConsoleError($"An error occurred while trying to claim the vote reward of the player {player?.displayName}:{player?.UserIDString}");
                ConsoleWarn($"URL: {MaskApiUrl(url)}");
                ConsoleWarn($"HTTP Code: {code} | Response: {response} | Server Name: {serverName}");
                ConsoleWarn("This error could be due to a malformed or incorrect server token, id, or player id / username issue. Most likely its due to your server key being incorrect. Check that your server key is correct.");
                return;
            }

            response = response?.Trim();

            _Debug("------------------------------");
            _Debug("Method: HandleClaimWebRequestCallback");
            _Debug($"Site: {site}");
            _Debug($"Code: {code}");
            _Debug($"Response: {response}");
            _Debug($"URL: {MaskApiUrl(url)}");
            _Debug($"ServerName: {serverName}");
            _Debug($"Player Name: {player?.displayName}");
            _Debug($"Player SteamID: {player?.UserIDString}");
            _Debug("Web Request Type: Claim");

            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                _Debug($"Verbose Debug Enabled, Setting Response Code to: {_config.DebugSettings[ConfigDefaultKeys.ClaimAPIRepsonseCode]}");
                response = _config.DebugSettings[ConfigDefaultKeys.ClaimAPIRepsonseCode];
            }

            if (response == "1")
            {
                BasePlayer rewardPlayer = player == null ? null : BasePlayer.FindByID(player.userID) ?? player;
                bool rewardConfigured = HandleVoteCount(rewardPlayer);
                string playerName = rewardPlayer?.displayName ?? rewardPlayer?.UserIDString ?? "Unknown";
                string playerSteamId = rewardPlayer?.UserIDString;
                string voteCount = GetStoredVoteCount(playerSteamId).ToString();

                if (rewardPlayer != null && rewardPlayer.IsConnected)
                {
                    string messageKey = rewardConfigured ? "ThankYou" : "ThankYouNoReward";
                    rewardPlayer.ChatMessage(_lang(messageKey, rewardPlayer.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], voteCount, site));
                }

                if (_config.Discord[ConfigDefaultKeys.DiscordEnabled].ToBool())
                {
                    string discordMessageKey = rewardConfigured ? "DiscordWebhookMessage" : "DiscordWebhookMessageNoReward";
                    ServerMgr.Instance.StartCoroutine(DiscordSendMessage(_lang(discordMessageKey, null, playerName, serverName, site)));
                }

                if (_config.NotificationSettings[ConfigDefaultKeys.GlobalChatAnnouncements].ToBool())
                {
                    string globalMessageKey = rewardConfigured ? "GlobalChatAnnouncements" : "GlobalChatAnnouncementsNoReward";

                    foreach (BasePlayer recipient in BasePlayer.activePlayerList)
                    {
                        if (recipient == null || !recipient.IsConnected)
                        {
                            continue;
                        }

                        recipient.ChatMessage(_lang(globalMessageKey, recipient.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], playerName, voteCount));
                    }
                }
            }
            else if (notifyPlayer && player != null && player.IsConnected && response == "2")
            {
                player.ChatMessage(_lang("AlreadyVoted", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site));
            }
            else if (notifyPlayer && player != null && player.IsConnected)
            {
                player.ChatMessage(_lang("ClaimStatus", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site, serverName));
            }
        }

        private void HandleStatusWebRequestCallback(int code, string response, BasePlayer player, string statusUrl, string claimUrl, string serverName, string site, bool notifyPlayer, string pendingStatusKey)
        {
            pendingStatusChecks.Remove(pendingStatusKey);

            if (code != 200)
            {
                ConsoleError($"An error occurred while trying to check the vote status of the player {player?.displayName}:{player?.UserIDString}");
                ConsoleWarn($"URL: {MaskApiUrl(statusUrl)}");
                ConsoleWarn($"HTTP Code: {code} | Response: {response} | Server Name: {serverName}");
                ConsoleWarn("This error could be due to a malformed or incorrect server token, id, or player id / username issue. Most likely its due to your server key being incorrect. Check that your server key is correct.");
                return;
            }

            response = response?.Trim();

            _Debug("------------------------------");
            _Debug("Method: HandleStatusWebRequestCallback");
            _Debug($"Site: {site}");
            _Debug($"Code: {code}");
            _Debug($"Response: {response}");
            _Debug($"URL: {MaskApiUrl(statusUrl)}");
            _Debug($"ServerName: {serverName}");
            _Debug($"Player Name: {player?.displayName}");
            _Debug($"Player SteamID: {player?.UserIDString}");
            _Debug("Web Request Type: Status/Check");

            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                _Debug($"Verbose Debug Enabled, Setting Response Code to: {_config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode]}");
                response = _config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode];
            }

            if (response == "1")
            {
                EnqueueClaimRequest(player, claimUrl, serverName, site, notifyPlayer);
            }
            else if (notifyPlayer && player != null && player.IsConnected && response == "0")
            {
                player.ChatMessage(_lang("NoRewards", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], serverName, site));
            }
            else if (notifyPlayer && player != null && player.IsConnected && response == "2")
            {
                player.ChatMessage(_lang("AlreadyVoted", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site));
            }
        }

        private void EnqueueClaimRequest(BasePlayer player, string claimUrl, string serverName, string site, bool notifyPlayer)
        {
            if (player == null)
            {
                return;
            }

            string pendingClaimKey = $"{player.UserIDString}:{serverName}:{site}";
            if (!pendingClaims.Add(pendingClaimKey))
            {
                _Debug($"A claim request is already pending for {player.displayName}/{player.UserIDString} on {site} ({serverName}).");
                return;
            }

            _Debug($"Automatic claim URL: {MaskApiUrl(claimUrl)}");

            QueueVoteRequest(() =>
            {
                try
                {
                    webrequest.Enqueue(claimUrl, null,
                        (code, response) => HandleClaimWebRequestCallback(code, response, player, claimUrl, serverName, site, notifyPlayer, pendingClaimKey), this,
                        RequestMethod.GET, null, 5000);
                }
                catch (Exception exception)
                {
                    pendingClaims.Remove(pendingClaimKey);
                    ConsoleError($"Failed to enqueue the claim request for {player.displayName}/{player.UserIDString} on {site}: {exception.Message}");
                }
            }, true);
        }

        private string FormatApiUrl(Dictionary<string, string> apiConfiguration, string apiKey, BasePlayer player, string serverId, string serverKey)
        {
            string apiLink = apiConfiguration[apiKey];
            bool usernameApiEnabled = apiConfiguration[ConfigDefaultKeys.apiUsername].ToBool();
            string encodedServerKey = Uri.EscapeDataString(serverKey);
            string encodedServerId = Uri.EscapeDataString(serverId);
            string encodedPlayerIdentifier = usernameApiEnabled
                ? Uri.EscapeDataString(player.displayName ?? string.Empty)
                : player.UserIDString;

            // Extra format arguments are ignored when the URL does not use them.
            // This allows every built-in or custom tracker to use:
            // {0} = API key/token, {1} = Steam ID or username, {2} = server ID.
            return string.Format(apiLink, encodedServerKey, encodedPlayerIdentifier, encodedServerId);
        }

        private bool TryGetVoteSiteConfiguration(string site, out string configuredSiteName, out Dictionary<string, string> apiConfiguration)
        {
            configuredSiteName = null;
            apiConfiguration = null;

            if (_config.VoteSitesAPI == null)
            {
                return false;
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> voteSite in _config.VoteSitesAPI)
            {
                if (!voteSite.Key.Equals(site, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                configuredSiteName = voteSite.Key;
                apiConfiguration = voteSite.Value;
                break;
            }

            if (apiConfiguration == null ||
                !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiClaim) ||
                !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiStatus) ||
                !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiLink) ||
                !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiUsername) ||
                string.IsNullOrWhiteSpace(apiConfiguration[ConfigDefaultKeys.apiClaim]) ||
                string.IsNullOrWhiteSpace(apiConfiguration[ConfigDefaultKeys.apiStatus]) ||
                string.IsNullOrWhiteSpace(apiConfiguration[ConfigDefaultKeys.apiLink]))
            {
                return false;
            }

            return true;
        }

        private bool TryParseServerCredentials(string configuredValue, out string serverId, out string serverKey)
        {
            serverId = null;
            serverKey = null;

            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return false;
            }

            string[] idKey = configuredValue.Split(new[] { ':' }, 2);
            if (idKey.Length != 2)
            {
                return false;
            }

            serverId = idKey[0].Trim();
            serverKey = idKey[1].Trim();

            if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(serverKey))
            {
                return false;
            }

            if (serverId.Equals("ID", StringComparison.OrdinalIgnoreCase) ||
                serverKey.Equals("KEY", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private string MaskApiUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return url;
            }

            string maskedUrl = url;
            string[] sensitiveParameters = { "key=", "server_token=", "token=" };

            foreach (string parameter in sensitiveParameters)
            {
                int searchIndex = 0;

                while (searchIndex < maskedUrl.Length)
                {
                    int parameterIndex = maskedUrl.IndexOf(parameter, searchIndex, StringComparison.OrdinalIgnoreCase);
                    if (parameterIndex < 0)
                    {
                        break;
                    }

                    int valueStart = parameterIndex + parameter.Length;
                    int valueEnd = maskedUrl.IndexOf('&', valueStart);
                    if (valueEnd < 0)
                    {
                        valueEnd = maskedUrl.Length;
                    }

                    maskedUrl = maskedUrl.Substring(0, valueStart) + "********" + maskedUrl.Substring(valueEnd);
                    searchIndex = valueStart + 8;
                }
            }

            return maskedUrl;
        }

        private bool HandleVoteCount(BasePlayer player)
        {
            if (player == null)
            {
                return false;
            }

            _Debug("------------------------------");
            _Debug("Method: HandleVoteCount");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            int playerVoteCount = GetStoredVoteCount(player.UserIDString);
            _Debug($"Current VoteCount: {playerVoteCount}");

            playerVoteCount += 1;
            DataFile[player.UserIDString] = playerVoteCount;
            SaveDataFile(DataFile);
            _Debug($"Updated Vote Count: {playerVoteCount}");

            bool cumulativeRewards = _config.PluginSettings[ConfigDefaultKeys.RewardIsCumulative].ToBool();
            bool rewardConfigured = HasConfiguredRewardForVote(playerVoteCount, cumulativeRewards);

            if (cumulativeRewards)
            {
                GiveCumulativeRewards(player, playerVoteCount);
            }
            else
            {
                GiveNormalRewards(player, playerVoteCount);
            }

            return rewardConfigured;
        }

        private int GetStoredVoteCount(string steamId)
        {
            if (string.IsNullOrEmpty(steamId) || DataFile[steamId] == null)
            {
                return 0;
            }

            object storedValue = DataFile[steamId];

            try
            {
                return Convert.ToInt32(storedValue);
            }
            catch
            {
                int parsedValue;
                return int.TryParse(storedValue.ToString(), out parsedValue) ? parsedValue : 0;
            }
        }

        private void GiveCumulativeRewards(BasePlayer player, int playerVoteCount)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveCumulativeRewards");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            GiveEveryReward(player);

            if (playerVoteCount == 1)
            {
                GiveFirstReward(player);
            }

            foreach (KeyValuePair<string, List<string>> rewards in _config.Rewards)
            {
                int requiredVoteCount;
                if (!TryGetNumericRewardVoteCount(rewards.Key, out requiredVoteCount))
                {
                    continue;
                }

                if (requiredVoteCount <= playerVoteCount)
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
                int requiredVoteCount;
                if (!TryGetNumericRewardVoteCount(rewards.Key, out requiredVoteCount))
                {
                    continue;
                }

                if (requiredVoteCount == playerVoteCount)
                {
                    GiveSubsequentReward(player, rewards.Value);
                }
            }
        }

        private bool TryGetNumericRewardVoteCount(string rewardKey, out int requiredVoteCount)
        {
            return int.TryParse(rewardKey, out requiredVoteCount) && requiredVoteCount > 0;
        }

        private bool HasRewardCommands(List<string> rewardCommands)
        {
            return rewardCommands != null && rewardCommands.Any(command => !string.IsNullOrWhiteSpace(command));
        }

        private bool HasConfiguredRewardForVote(int playerVoteCount, bool cumulativeRewards)
        {
            List<string> rewardCommands;

            if (_config.Rewards.TryGetValue("@", out rewardCommands) && HasRewardCommands(rewardCommands))
            {
                return true;
            }

            if (playerVoteCount == 1 &&
                _config.Rewards.TryGetValue("first", out rewardCommands) &&
                HasRewardCommands(rewardCommands))
            {
                return true;
            }

            foreach (KeyValuePair<string, List<string>> reward in _config.Rewards)
            {
                int requiredVoteCount;
                if (!TryGetNumericRewardVoteCount(reward.Key, out requiredVoteCount) || !HasRewardCommands(reward.Value))
                {
                    continue;
                }

                if (cumulativeRewards ? requiredVoteCount <= playerVoteCount : requiredVoteCount == playerVoteCount)
                {
                    return true;
                }
            }

            return false;
        }

        private string GetRewardDescription(string rewardKey, string playerId)
        {
            string description;
            if (_config.RewardDescriptions.TryGetValue(rewardKey, out description) && !string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            return _lang("RewardDescriptionMissing", playerId);
        }

        private int GetRewardDisplayOrder(string rewardKey)
        {
            if (rewardKey == "@")
            {
                return int.MinValue;
            }

            if (rewardKey == "first")
            {
                return int.MinValue + 1;
            }

            int requiredVoteCount;
            return TryGetNumericRewardVoteCount(rewardKey, out requiredVoteCount) ? requiredVoteCount : int.MaxValue;
        }

        private void GiveEveryReward(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveEveryReward");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            List<string> rewards;
            if (!_config.Rewards.TryGetValue("@", out rewards) || rewards == null)
            {
                return;
            }

            foreach (string rewardCommand in rewards)
            {
                RunOrQueueRewardCommand(player, rewardCommand);
            }
        }

        private void GiveFirstReward(BasePlayer player)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveFirstReward");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            List<string> rewards;
            if (!_config.Rewards.TryGetValue("first", out rewards) || rewards == null)
            {
                return;
            }

            foreach (string rewardCommand in rewards)
            {
                RunOrQueueRewardCommand(player, rewardCommand);
            }
        }

        private void GiveSubsequentReward(BasePlayer player, List<string> rewardsList)
        {
            _Debug("------------------------------");
            _Debug("Method: GiveSubsequentReward");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");
            _Debug($"Vote Count: {GetStoredVoteCount(player.UserIDString)}");

            if (rewardsList == null)
            {
                return;
            }

            foreach (string rewardCommand in rewardsList)
            {
                RunOrQueueRewardCommand(player, rewardCommand);
            }
        }

        private void RunOrQueueRewardCommand(BasePlayer player, string rewardCommand)
        {
            if (player == null || string.IsNullOrWhiteSpace(rewardCommand))
            {
                return;
            }

            string command = ParseRewardCommand(player, rewardCommand);
            _Debug($"Reward Command: {command}");

            if (player.IsConnected)
            {
                rust.RunServerCommand(command);
                return;
            }

            QueuePendingRewardCommand(player.UserIDString, command);
        }

        private void QueuePendingRewardCommand(string steamId, string command)
        {
            List<string> commands;
            if (!pendingRewardCommands.TryGetValue(steamId, out commands))
            {
                commands = new List<string>();
                pendingRewardCommands[steamId] = commands;
            }

            commands.Add(command);
            SavePendingRewardsData();
            _Debug($"Queued a pending reward command for {steamId}: {command}");
        }

        private void DeliverPendingRewards(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
            {
                return;
            }

            List<string> commands;
            if (!pendingRewardCommands.TryGetValue(player.UserIDString, out commands) || commands == null || commands.Count == 0)
            {
                return;
            }

            List<string> commandsToRun = commands.ToList();

            foreach (string command in commandsToRun)
            {
                _Debug($"Delivering pending reward command to {player.displayName}/{player.UserIDString}: {command}");
                rust.RunServerCommand(command);
            }

            pendingRewardCommands.Remove(player.UserIDString);
            SavePendingRewardsData();

            player.ChatMessage(_lang("PendingRewardsDelivered", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
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

        private void WarnConfigurationOnce(string warningKey, string message)
        {
            if (reportedConfigurationWarnings.Add(warningKey))
            {
                ConsoleWarn(message);
            }
        }

        private void CheckVotingStatus(BasePlayer player, bool notifyPlayer = true)
        {
            if (player == null || !player.IsConnected)
            {
                return;
            }

            _Debug("------------------------------");
            _Debug("Method: CheckVotingStatus");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            if (notifyPlayer && _config.NotificationSettings[ConfigDefaultKeys.PleaseWaitMessage].ToBool())
            {
                player.ChatMessage(_lang("PleaseWait", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> server in _config.Servers)
            {
                if (server.Value == null)
                {
                    continue;
                }

                _Debug($"Server: {server.Key}");

                foreach (KeyValuePair<string, string> configuredVoteSite in server.Value)
                {
                    string configuredSiteName;
                    Dictionary<string, string> apiConfiguration;

                    if (!TryGetVoteSiteConfiguration(configuredVoteSite.Key, out configuredSiteName, out apiConfiguration))
                    {
                        WarnConfigurationOnce($"{server.Key}:{configuredVoteSite.Key}", $"The voting website {configuredVoteSite.Key} on {server.Key} is missing or has an incomplete API configuration.");
                        continue;
                    }

                    string serverId;
                    string serverKey;

                    if (!TryParseServerCredentials(configuredVoteSite.Value, out serverId, out serverKey))
                    {
                        _Debug($"Skipping unconfigured voting website {configuredVoteSite.Key} on {server.Key}.");
                        continue;
                    }

                    string formattedStatusUrl;
                    string formattedClaimUrl;

                    try
                    {
                        formattedStatusUrl = FormatApiUrl(apiConfiguration, ConfigDefaultKeys.apiStatus, player, serverId, serverKey);
                        formattedClaimUrl = FormatApiUrl(apiConfiguration, ConfigDefaultKeys.apiClaim, player, serverId, serverKey);
                    }
                    catch (Exception exception)
                    {
                        ConsoleError($"Failed to format the API URL for {configuredSiteName} on {server.Key}: {exception.Message}");
                        continue;
                    }

                    BasePlayer requestPlayer = player;
                    string requestServerName = server.Key;
                    string requestSiteName = configuredSiteName;
                    string statusUrl = formattedStatusUrl;
                    string claimUrl = formattedClaimUrl;
                    bool requestNotifyPlayer = notifyPlayer;
                    string pendingStatusKey = $"{requestPlayer.UserIDString}:{requestServerName}:{requestSiteName}";

                    if (!pendingStatusChecks.Add(pendingStatusKey))
                    {
                        _Debug($"A status request is already pending for {requestPlayer.displayName}/{requestPlayer.UserIDString} on {requestSiteName} ({requestServerName}).");
                        continue;
                    }

                    _Debug($"Status URL: {MaskApiUrl(statusUrl)}");
                    _Debug($"Claim URL: {MaskApiUrl(claimUrl)}");

                    QueueVoteRequest(() =>
                    {
                        if (requestPlayer == null || !requestPlayer.IsConnected)
                        {
                            pendingStatusChecks.Remove(pendingStatusKey);
                            return;
                        }

                        try
                        {
                            webrequest.Enqueue(statusUrl, null,
                                (code, response) => HandleStatusWebRequestCallback(code, response, requestPlayer, statusUrl, claimUrl, requestServerName, requestSiteName, requestNotifyPlayer, pendingStatusKey), this,
                                RequestMethod.GET, null, 5000);
                        }
                        catch (Exception exception)
                        {
                            pendingStatusChecks.Remove(pendingStatusKey);
                            ConsoleError($"Failed to enqueue the status request for {requestPlayer.displayName}/{requestPlayer.UserIDString} on {requestSiteName}: {exception.Message}");
                        }
                    });
                }
            }
        }

        private void StartRequestQueueProcessor()
        {
            timer.Every(VoteRequestSpacing, () =>
            {
                if (claimRequestQueue.Count == 0 && voteRequestQueue.Count == 0)
                {
                    return;
                }

                Action queuedRequest = claimRequestQueue.Count > 0
                    ? claimRequestQueue.Dequeue()
                    : voteRequestQueue.Dequeue();

                try
                {
                    queuedRequest?.Invoke();
                }
                catch (Exception exception)
                {
                    ConsoleError($"An error occurred while processing the vote request queue: {exception.Message}");
                }
            });

            ConsoleLog($"Vote API requests will be distributed at one request every {VoteRequestSpacing:0.0} seconds.");
        }

        private void QueueVoteRequest(Action request, bool priority = false)
        {
            if (request == null)
            {
                return;
            }

            if (priority)
            {
                claimRequestQueue.Enqueue(request);
                return;
            }

            voteRequestQueue.Enqueue(request);
        }

        private bool IsCommandOnCooldown(BasePlayer player, string commandName, out int remainingSeconds)
        {
            remainingSeconds = 0;

            if (player == null)
            {
                return true;
            }

            string cooldownKey = $"{player.UserIDString}:{commandName}";
            float currentTime = UnityEngine.Time.realtimeSinceStartup;
            float cooldownEnd;

            if (commandCooldowns.TryGetValue(cooldownKey, out cooldownEnd) && cooldownEnd > currentTime)
            {
                remainingSeconds = Math.Max(1, (int)Math.Ceiling(cooldownEnd - currentTime));
                return true;
            }

            commandCooldowns[cooldownKey] = currentTime + ManualCommandCooldown;
            return false;
        }

        private void ScheduleVoteFollowUpCheck(BasePlayer player)
        {
            int delay = GetVoteFollowUpCheckDelay();
            if (delay <= 0 || player == null || !scheduledVoteChecks.Add(player.userID))
            {
                return;
            }

            timer.Once(delay, () =>
            {
                scheduledVoteChecks.Remove(player.userID);

                if (player != null && player.IsConnected)
                {
                    CheckVotingStatus(player, false);
                }
            });
        }

        private void StartAutomaticVoteChecks()
        {
            int interval = GetAutomaticVoteCheckInterval();
            if (interval <= 0)
            {
                ConsoleLog("Automatic vote checks for online players are disabled.");
                return;
            }

            timer.Every(interval, () =>
            {
                if (voteRequestQueue.Count > 0)
                {
                    _Debug($"Skipping the automatic vote check cycle because {voteRequestQueue.Count} status request(s) are still queued.");
                    return;
                }

                foreach (BasePlayer player in BasePlayer.activePlayerList.ToArray())
                {
                    if (player == null || !player.IsConnected)
                    {
                        continue;
                    }

                    CheckIfPlayerDataExists(player);
                    DeliverPendingRewards(player);
                    CheckVotingStatus(player, false);
                }
            });

            ConsoleLog($"Automatic vote checks for online players will run every {interval} seconds.");
        }

        private int GetAutomaticVoteCheckInterval()
        {
            string configuredInterval;
            int interval;

            if (_config.NotificationSettings.TryGetValue(ConfigDefaultKeys.AutomaticVoteCheckInterval, out configuredInterval) &&
                int.TryParse(configuredInterval, out interval))
            {
                return Math.Max(0, interval);
            }

            return DefaultAutomaticVoteCheckInterval;
        }

        private int GetVoteFollowUpCheckDelay()
        {
            string configuredDelay;
            int delay;

            if (_config.NotificationSettings.TryGetValue(ConfigDefaultKeys.VoteFollowUpCheckDelay, out configuredDelay) &&
                int.TryParse(configuredDelay, out delay))
            {
                return Math.Max(0, delay);
            }

            return DefaultVoteFollowUpCheckDelay;
        }

        protected void ConsoleLog(object message)
        {
            Puts(message?.ToString());
        }

        private bool IsLoggingEnabled()
        {
            string loggingSetting;
            bool loggingEnabled;

            return _config != null &&
                   _config.PluginSettings != null &&
                   _config.PluginSettings.TryGetValue(ConfigDefaultKeys.LogEnabled, out loggingSetting) &&
                   bool.TryParse(loggingSetting, out loggingEnabled) &&
                   loggingEnabled;
        }

        protected void ConsoleError(string message)
        {
            if (IsLoggingEnabled())
            {
                LogToFile("EasyVoteExtended", $"ERROR: {message}", this);
            }

            Debug.LogError($"ERROR: {message}");
        }

        protected void ConsoleWarn(string message)
        {
            if (IsLoggingEnabled())
            {
                LogToFile("EasyVoteExtended", $"WARNING: {message}", this);
            }

            Debug.LogWarning($"WARNING: {message}");
        }

        protected void _Debug(string message, string arg = null)
        {
            string debugSetting;
            bool debugEnabled;

            if (_config == null ||
                _config.DebugSettings == null ||
                !_config.DebugSettings.TryGetValue(ConfigDefaultKeys.DebugEnabled, out debugSetting) ||
                !bool.TryParse(debugSetting, out debugEnabled) ||
                !debugEnabled)
            {
                return;
            }

            if (IsLoggingEnabled())
            {
                LogToFile("EasyVoteExtended", $"DEBUG: {message}", this);
            }

            Puts($"DEBUG: {message}");

            if (arg != null)
            {
                Puts($"DEBUG ARG: {arg}");
            }
        }

        private IEnumerator DiscordSendMessage(string msg)
        {
            string webhookUrl = _config.Discord[ConfigDefaultKeys.discordWebhookURL];
            const string defaultWebhookUrl = "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks";

            if (string.IsNullOrWhiteSpace(webhookUrl) || webhookUrl == defaultWebhookUrl)
            {
                yield break;
            }

            string webhookTitle;
            string webhookContent = msg;

            if (_config.Discord.TryGetValue(ConfigDefaultKeys.discordTitle, out webhookTitle) && !string.IsNullOrWhiteSpace(webhookTitle))
            {
                webhookContent = $"**{webhookTitle}**\n{msg}";
            }

            WWWForm formData = new WWWForm();
            formData.AddField("content", $"{webhookContent}\n");

            using (UnityWebRequest request = UnityWebRequest.Post(webhookUrl, formData))
            {
                yield return request.SendWebRequest();

                if ((request.isNetworkError || request.isHttpError) &&
                    !string.IsNullOrEmpty(request.error) &&
                    request.error.Contains("Too Many Requests"))
                {
                    Puts("Discord Webhook Rate Limit Exceeded...");
                }
            }
        }

        ////////////////////////////////////////////////////////////
        // Commands
        ////////////////////////////////////////////////////////////

        [ChatCommand("rewardlist")]
        private void RewardListChatCommand(BasePlayer player, string command, string[] args)
        {
            player.ChatMessage(_lang("RewardsListHeader", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));

            List<KeyValuePair<string, List<string>>> configuredRewards = _config.Rewards
                .Where(reward => HasRewardCommands(reward.Value) &&
                                 (reward.Key == "@" || reward.Key == "first" || IsPositiveNumericRewardKey(reward.Key)))
                .OrderBy(reward => GetRewardDisplayOrder(reward.Key))
                .ThenBy(reward => reward.Key)
                .ToList();

            if (configuredRewards.Count == 0)
            {
                player.ChatMessage(_lang("NoConfiguredRewards", player.UserIDString));
                return;
            }

            foreach (KeyValuePair<string, List<string>> reward in configuredRewards)
            {
                string description = GetRewardDescription(reward.Key, player.UserIDString);

                if (reward.Key == "@")
                {
                    player.ChatMessage(_lang("EveryVote", player.UserIDString, description));
                }
                else if (reward.Key == "first")
                {
                    player.ChatMessage(_lang("FirstVote", player.UserIDString, description));
                }
                else
                {
                    player.ChatMessage(_lang("NumberVote", player.UserIDString, reward.Key, description));
                }
            }
        }

        private bool IsPositiveNumericRewardKey(string rewardKey)
        {
            int requiredVoteCount;
            return TryGetNumericRewardVoteCount(rewardKey, out requiredVoteCount);
        }

        [ChatCommand("vote")]
        private void VoteChatCommand(BasePlayer player, string command, string[] args)
        {
            int remainingSeconds;
            if (IsCommandOnCooldown(player, command, out remainingSeconds))
            {
                player.ChatMessage(_lang("CommandCooldown", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], remainingSeconds));
                return;
            }

            player.ChatMessage(_lang("VoteList", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));

            foreach (KeyValuePair<string, Dictionary<string, string>> server in _config.Servers)
            {
                if (server.Value == null)
                {
                    continue;
                }

                string customLink;
                if (_config.ServersCustomLink.TryGetValue(server.Key, out customLink) && !string.IsNullOrWhiteSpace(customLink))
                {
                    player.ChatMessage(_lang("VoteLinkCustom", player.UserIDString, server.Key, customLink));
                    continue;
                }

                foreach (KeyValuePair<string, string> configuredVoteSite in server.Value)
                {
                    string configuredSiteName;
                    Dictionary<string, string> apiConfiguration;
                    string serverId;
                    string serverKey;

                    if (!TryGetVoteSiteConfiguration(configuredVoteSite.Key, out configuredSiteName, out apiConfiguration) ||
                        !TryParseServerCredentials(configuredVoteSite.Value, out serverId, out serverKey))
                    {
                        continue;
                    }

                    string voteLink;

                    try
                    {
                        voteLink = string.Format(apiConfiguration[ConfigDefaultKeys.apiLink], Uri.EscapeDataString(serverId));
                    }
                    catch (Exception exception)
                    {
                        ConsoleError($"Failed to format the vote link for {configuredSiteName} on {server.Key}: {exception.Message}");
                        continue;
                    }

                    player.ChatMessage(_lang("VoteLink", player.UserIDString, server.Key, configuredSiteName, voteLink));
                }
            }

            player.ChatMessage(_lang("EarnRewardAutomatic", player.UserIDString));
            ScheduleVoteFollowUpCheck(player);
        }

        [ChatCommand("claim")]
        private void ClaimChatCommand(BasePlayer player, string command, string[] args)
        {
            int remainingSeconds;
            if (IsCommandOnCooldown(player, command, out remainingSeconds))
            {
                player.ChatMessage(_lang("CommandCooldown", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], remainingSeconds));
                return;
            }

            CheckIfPlayerDataExists(player);
            DeliverPendingRewards(player);

            _Debug("------------------------------");
            _Debug("Method: ClaimChatCommand");
            _Debug($"Player: {player.displayName}/{player.UserIDString}");

            // Kept for backwards compatibility. Rewards are claimed automatically,
            // but this command can still force an immediate vote status check.
            CheckVotingStatus(player);
        }

        [ConsoleCommand("eve.clearvote")]
        private void ClearPlayerVoteCountConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!EnsureServerConsoleCommand(arg, "eve.clearvote"))
            {
                return;
            }

            if (arg == null || arg.Args == null || arg.Args.Length != 1)
            {
                ReplyConsoleLocalized(arg, "ConsoleClearVoteUsage", "eve.clearvote");
                return;
            }

            string targetInput = arg.GetString(0);
            BasePlayer player = arg.GetPlayer(0);
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", targetInput);
                return;
            }

            DataFile[player.UserIDString] = 0;
            SaveDataFile(DataFile);
            ReplyConsoleLocalized(arg, "ConsoleClearVoteSuccess", FormatPlayerForConsole(player));
        }

        [ConsoleCommand("eve.checkvote")]
        private void CheckPlayerVoteCountConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!EnsureServerConsoleCommand(arg, "eve.checkvote"))
            {
                return;
            }

            if (arg == null || arg.Args == null || arg.Args.Length != 1)
            {
                ReplyConsoleLocalized(arg, "ConsoleCheckVoteUsage", "eve.checkvote");
                return;
            }

            string targetInput = arg.GetString(0);
            BasePlayer player = arg.GetPlayer(0);
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", targetInput);
                return;
            }

            ReplyConsoleLocalized(arg, "ConsoleCheckVoteSuccess", FormatPlayerForConsole(player), getPlayerVotes(player.UserIDString));
        }

        [ConsoleCommand("eve.setvote")]
        private void SetPlayerVoteCountConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!EnsureServerConsoleCommand(arg, "eve.setvote"))
            {
                return;
            }

            if (arg == null || arg.Args == null || arg.Args.Length != 2)
            {
                ReplyConsoleLocalized(arg, "ConsoleSetVoteUsage", "eve.setvote");
                return;
            }

            string targetInput = arg.GetString(0);
            BasePlayer player = arg.GetPlayer(0);
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", targetInput);
                return;
            }

            int voteCount;
            if (!int.TryParse(arg.GetString(1), out voteCount) || voteCount < 0)
            {
                ReplyConsoleLocalized(arg, "ConsoleInvalidVoteCount", arg.GetString(1));
                return;
            }

            DataFile[player.UserIDString] = voteCount;
            SaveDataFile(DataFile);
            ReplyConsoleLocalized(arg, "ConsoleSetVoteSuccess", FormatPlayerForConsole(player), voteCount);
        }

        [ConsoleCommand("eve.resetvotedata")]
        private void ResetAllVoteDataConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!EnsureServerConsoleCommand(arg, "eve.resetvotedata"))
            {
                return;
            }

            if (arg != null && arg.Args != null && arg.Args.Length > 0)
            {
                ReplyConsoleLocalized(arg, "ConsoleResetVoteDataUsage", "eve.resetvotedata");
                return;
            }

            int resetPlayers = DataFile.ToList().Count;
            ResetAllVoteData();
            ReplyConsoleLocalized(arg, "ConsoleResetVoteDataSuccess", resetPlayers);
        }

        private bool EnsureServerConsoleCommand(ConsoleSystem.Arg arg, string command)
        {
            BasePlayer player = arg?.Player();
            if (player == null)
            {
                return true;
            }

            ReplyPlayerConsoleLocalized(player, "ConsoleServerOnly", command);
            return false;
        }

        private string FormatPlayerForConsole(BasePlayer player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return $"{player.displayName} ({player.UserIDString})";
        }

        private void ReplyConsoleLocalized(ConsoleSystem.Arg arg, string key, params object[] args)
        {
            string message = _lang(key, null, args);

            if (arg != null)
            {
                arg.ReplyWith(message);
                return;
            }

            ConsoleLog(message);
        }

        private void ReplyPlayerConsoleLocalized(BasePlayer player, string key, params object[] args)
        {
            if (player == null)
            {
                return;
            }

            player.SendConsoleCommand("echo", _lang(key, player.UserIDString, args));
        }

        ////////////////////////////////////////////////////////////
        // Configs
        ////////////////////////////////////////////////////////////
        
        private PluginConfig _config;

        protected override void SaveConfig() => Config.WriteObject(_config);

        private string _lang(string key, string id = null, params object[] args)
        {
            string message = lang.GetMessage(key, this, id);

            try
            {
                return string.Format(message, args);
            }
            catch (FormatException)
            {
                ConsoleWarn($"Language message '{key}' contains invalid formatting for target '{(id == null ? "server default" : id)}'.");
                return message;
            }
        }

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
            public const string AutomaticVoteCheckInterval = "Automatic vote check interval for online players (seconds, 0 to disable)";
            public const string VoteFollowUpCheckDelay = "Vote follow-up check delay after using /vote (seconds, 0 to disable)";

            public const string DebugEnabled = "Debug Enabled?";
            public const string VerboseDebugEnabled = "Enable Verbose Debugging?";
            public const string CheckAPIResponseCode = "Set Check API Response Code (0 = Not found, 1 = Has voted and not claimed, 2 = Has voted and claimed)";
            public const string ClaimAPIRepsonseCode = "Set Claim API Response Code (0 = Not found, 1 = Has voted and not claimed. The vote will now be set as claimed., 2 = Has voted and claimed";
        }
        
        private class PluginConfig
        {
            [JsonProperty(PropertyName = "Configuration Version")]
            public int ConfigurationVersion;

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

        private Dictionary<string, string> GetRustServerListApiConfiguration()
        {
            return new Dictionary<string, string>
            {
                { ConfigDefaultKeys.apiClaim, "https://rustserverlist.com/api/vote?action=claim&key={0}&steamid={1}" },
                { ConfigDefaultKeys.apiStatus, "https://rustserverlist.com/api/vote?action=status&key={0}&steamid={1}" },
                { ConfigDefaultKeys.apiLink, "https://rustserverlist.com/server/{0}" },
                { ConfigDefaultKeys.apiUsername, "false" }
            };
        }

        private bool AddMissingSettings(Dictionary<string, string> currentSettings, Dictionary<string, string> defaultSettings)
        {
            bool settingsChanged = false;

            foreach (KeyValuePair<string, string> setting in defaultSettings)
            {
                if (currentSettings.ContainsKey(setting.Key))
                {
                    continue;
                }

                currentSettings[setting.Key] = setting.Value;
                settingsChanged = true;
            }

            return settingsChanged;
        }

        private void EnsureConfigDefaults()
        {
            if (_config == null)
            {
                LoadDefaultConfig();
                return;
            }

            bool configChanged = false;

            if (_config.DebugSettings == null)
            {
                _config.DebugSettings = new Dictionary<string, string>();
                configChanged = true;
            }

            configChanged |= AddMissingSettings(_config.DebugSettings, new Dictionary<string, string>
            {
                { ConfigDefaultKeys.DebugEnabled, "false" },
                { ConfigDefaultKeys.VerboseDebugEnabled, "false" },
                { ConfigDefaultKeys.CheckAPIResponseCode, "0" },
                { ConfigDefaultKeys.ClaimAPIRepsonseCode, "0" }
            });

            if (_config.PluginSettings == null)
            {
                _config.PluginSettings = new Dictionary<string, string>();
                configChanged = true;
            }

            configChanged |= AddMissingSettings(_config.PluginSettings, new Dictionary<string, string>
            {
                { ConfigDefaultKeys.LogEnabled, "true" },
                { ConfigDefaultKeys.ClearRewardsOnWipe, "false" },
                { ConfigDefaultKeys.RewardIsCumulative, "false" },
                { ConfigDefaultKeys.Prefix, "<color=#e67e22>[EasyVote]</color> " }
            });

            if (_config.NotificationSettings == null)
            {
                _config.NotificationSettings = new Dictionary<string, string>();
                configChanged = true;
            }

            configChanged |= AddMissingSettings(_config.NotificationSettings, new Dictionary<string, string>
            {
                { ConfigDefaultKeys.GlobalChatAnnouncements, "true" },
                { ConfigDefaultKeys.PleaseWaitMessage, "true" },
                { ConfigDefaultKeys.OnPlayerSleepEnded, "false" },
                { ConfigDefaultKeys.OnPlayerConnected, "true" },
                { ConfigDefaultKeys.AutomaticVoteCheckInterval, DefaultAutomaticVoteCheckInterval.ToString() },
                { ConfigDefaultKeys.VoteFollowUpCheckDelay, DefaultVoteFollowUpCheckDelay.ToString() }
            });

            if (_config.Discord == null)
            {
                _config.Discord = new Dictionary<string, string>();
                configChanged = true;
            }

            configChanged |= AddMissingSettings(_config.Discord, new Dictionary<string, string>
            {
                { ConfigDefaultKeys.discordWebhookURL, "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks" },
                { ConfigDefaultKeys.DiscordEnabled, "false" },
                { ConfigDefaultKeys.discordTitle, "A player has just voted for us!" }
            });

            if (_config.Rewards == null)
            {
                _config.Rewards = new Dictionary<string, List<string>>();
                configChanged = true;
            }

            if (_config.RewardDescriptions == null)
            {
                _config.RewardDescriptions = new Dictionary<string, string>();
                configChanged = true;
            }

            if (_config.Servers == null)
            {
                _config.Servers = new Dictionary<string, Dictionary<string, string>>();
                configChanged = true;
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> server in _config.Servers.ToList())
            {
                if (server.Value == null)
                {
                    _config.Servers[server.Key] = new Dictionary<string, string>();
                    configChanged = true;
                }
            }

            if (_config.ServersCustomLink == null)
            {
                _config.ServersCustomLink = new Dictionary<string, string>();
                configChanged = true;
            }

            if (_config.VoteSitesAPI == null)
            {
                _config.VoteSitesAPI = new Dictionary<string, Dictionary<string, string>>();
                configChanged = true;
            }

            if (_config.ConfigurationVersion < CurrentConfigurationVersion)
            {
                foreach (KeyValuePair<string, Dictionary<string, string>> server in _config.Servers)
                {
                    Dictionary<string, string> serverVoteSites = server.Value;
                    bool hasRustServerList = serverVoteSites.Keys.Any(key => key.Equals("RustServerList.com", StringComparison.OrdinalIgnoreCase));

                    if (!hasRustServerList)
                    {
                        serverVoteSites["RustServerList.com"] = "ID:KEY";
                        configChanged = true;
                    }
                }

                string rustServerListKey = _config.VoteSitesAPI.Keys.FirstOrDefault(key => key.Equals("RustServerList.com", StringComparison.OrdinalIgnoreCase));
                if (rustServerListKey == null)
                {
                    _config.VoteSitesAPI["RustServerList.com"] = GetRustServerListApiConfiguration();
                    configChanged = true;
                }
                else
                {
                    if (_config.VoteSitesAPI[rustServerListKey] == null)
                    {
                        _config.VoteSitesAPI[rustServerListKey] = new Dictionary<string, string>();
                        configChanged = true;
                    }

                    configChanged |= AddMissingSettings(_config.VoteSitesAPI[rustServerListKey], GetRustServerListApiConfiguration());
                }

                _config.ConfigurationVersion = CurrentConfigurationVersion;
                configChanged = true;
            }

            string topGamesKey = _config.VoteSitesAPI.Keys.FirstOrDefault(key => key.Equals("Top-Games.net", StringComparison.OrdinalIgnoreCase));
            if (topGamesKey != null && _config.VoteSitesAPI[topGamesKey] != null)
            {
                Dictionary<string, string> topGamesConfiguration = _config.VoteSitesAPI[topGamesKey];
                string claimUrl;
                string statusUrl;

                bool usesUsernameEndpoint =
                    topGamesConfiguration.TryGetValue(ConfigDefaultKeys.apiClaim, out claimUrl) &&
                    topGamesConfiguration.TryGetValue(ConfigDefaultKeys.apiStatus, out statusUrl) &&
                    ((claimUrl != null && claimUrl.IndexOf("playername={1}", StringComparison.OrdinalIgnoreCase) >= 0) ||
                     (statusUrl != null && statusUrl.IndexOf("playername={1}", StringComparison.OrdinalIgnoreCase) >= 0));

                string usernameSetting;
                bool usernameEnabled =
                    topGamesConfiguration.TryGetValue(ConfigDefaultKeys.apiUsername, out usernameSetting) &&
                    string.Equals(usernameSetting, "true", StringComparison.OrdinalIgnoreCase);

                if (usesUsernameEndpoint && !usernameEnabled)
                {
                    topGamesConfiguration[ConfigDefaultKeys.apiUsername] = "true";
                    configChanged = true;
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> voteSite in _config.VoteSitesAPI.ToList())
            {
                if (voteSite.Value == null)
                {
                    _config.VoteSitesAPI[voteSite.Key] = new Dictionary<string, string>();
                    configChanged = true;
                }
            }

            if (configChanged)
            {
                SaveConfig();
                Puts("Easy Vote Extended configuration was checked and updated with missing defaults.");
            }
        }

        protected override void LoadDefaultConfig()
        {
            
            _config = new PluginConfig();
            _config.ConfigurationVersion = CurrentConfigurationVersion;
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
                {ConfigDefaultKeys.OnPlayerConnected, "true"},
                {ConfigDefaultKeys.AutomaticVoteCheckInterval, DefaultAutomaticVoteCheckInterval.ToString()},
                {ConfigDefaultKeys.VoteFollowUpCheckDelay, DefaultVoteFollowUpCheckDelay.ToString()}
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
                { "ServerName1", new Dictionary<string, string>() { { "Rust-Servers.net", "ID:KEY" }, { "Rustservers.gg", "ID:KEY" }, { "BestServers.com", "ID:KEY" }, { "GamesFinder.net", "ID:KEY" }, { "Top-Games.net", "ID:KEY" }, { "TrackyServer.com", "ID:KEY" }, { "RustServerList.com", "ID:KEY" } } },
                { "ServerName2", new Dictionary<string, string>() { { "Rust-Servers.net", "ID:KEY" }, { "Rustservers.gg", "ID:KEY" }, { "BestServers.com", "ID:KEY" }, { "GamesFinder.net", "ID:KEY" }, { "Top-Games.net", "ID:KEY" }, { "TrackyServer.com", "ID:KEY" }, { "RustServerList.com", "ID:KEY" } } }
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
                        { ConfigDefaultKeys.apiUsername, "true"}
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
                },
                {
                    "RustServerList.com",
                    GetRustServerListApiConfiguration()
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
                }
                else
                {
                    EnsureConfigDefaults();
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
                ["CommandCooldown"] = "{0} Please wait <color=#e67e22>{1}</color> second(s) before using this command again.",
                ["PendingRewardsDelivered"] = "{0} Your pending vote reward(s) have been delivered!",
                ["ClaimStatus"] = "{0} <color=#e67e22>{1}</color> reports you have not voted yet on <color=#e67e22>{2}</color>. Vote now!",
                ["PleaseWait"] = "{0} Checking all the vote sites API's... Please be patient as this can take some time...",
                ["VoteList"] = "{0} You can vote for our server at the following links:",
                ["EarnRewardAutomatic"] = "Your reward will be claimed automatically while you are online or the next time you connect!",
                ["ThankYou"] = "{0} Thank you for voting! You have voted <color=#e67e22>{1}</color> time(s) Here is your reward for: <color=#e67e22>{2}</color>",
                ["ThankYouNoReward"] = "{0} Thank you for voting! Your vote on <color=#e67e22>{2}</color> was recorded, bringing your total to <color=#e67e22>{1}</color>. No reward is configured for this vote milestone.",
                ["NoRewards"] = "{0} You haven't voted for <color=#e67e22>{1}</color> on <color=#e67e22>{2}</color> yet! Type <color=#e67e22>/vote</color> to get started!",
                ["GlobalChatAnnouncements"] = "{0} <color=#e67e22>{1}</color> has voted <color=#e67e22>{2}</color> time(s) and just received their rewards. Find out where you can vote by typing <color=#e67e22>/vote</color>\nTo see a list of available rewards type <color=#e67e22>/rewardlist</color>",
                ["GlobalChatAnnouncementsNoReward"] = "{0} <color=#e67e22>{1}</color> has voted <color=#e67e22>{2}</color> time(s). Find out where you can vote by typing <color=#e67e22>/vote</color>.",
                ["AlreadyVoted"] = "{0} <color=#e67e22>{1}</color> reports you have already voted! Vote again later.",
                ["DiscordWebhookMessage"] = "{0} has voted for {1} on {2} and got some rewards! Type /rewardlist in game to find out what you can get when you vote for us!",
                ["DiscordWebhookMessageNoReward"] = "{0} has voted for {1} on {2}. The vote was recorded successfully.",
                ["RewardsListHeader"] = "{0} The following rewards are given for voting!",
                ["NoConfiguredRewards"] = "No vote rewards are currently configured.",
                ["RewardDescriptionMissing"] = "Description not configured",
                ["EveryVote"] = "Every Vote: <color=#e67e22>{0}</color>",
                ["FirstVote"] = "First Vote: <color=#e67e22>{0}</color>",
                ["NumberVote"] = "Vote no. {0}: <color=#e67e22>{1}</color>",
                ["VoteLink"] = "{0} ({1}): <color=#e67e22>{2}</color>",
                ["VoteLinkCustom"] = "{0}: <color=#e67e22>{1}</color>",
                ["ConsoleServerOnly"] = "The command '{0}' can only be executed from the server console or RCON.",
                ["ConsolePlayerNotFound"] = "No player was found matching '{0}'.",
                ["ConsoleClearVoteUsage"] = "Usage: {0} <steamid|username>",
                ["ConsoleClearVoteSuccess"] = "Vote count for {0} has been reset to 0.",
                ["ConsoleCheckVoteUsage"] = "Usage: {0} <steamid|username>",
                ["ConsoleCheckVoteSuccess"] = "{0} has {1} total vote(s).",
                ["ConsoleSetVoteUsage"] = "Usage: {0} <steamid|username> [vote count]",
                ["ConsoleInvalidVoteCount"] = "'{0}' is not a valid vote count. Enter a whole number greater than or equal to 0.",
                ["ConsoleSetVoteSuccess"] = "Vote count for {0} has been set to {1}.",
                ["ConsoleResetVoteDataUsage"] = "Usage: {0}",
                ["ConsoleResetVoteDataSuccess"] = "Vote data has been reset for {0} player(s)."
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["CommandCooldown"] = "{0} Te rugăm să aștepți <color=#e67e22>{1}</color> secunde înainte de a folosi din nou această comandă.",
                ["PendingRewardsDelivered"] = "{0} Recompensele de vot aflate în așteptare au fost acordate!",
                ["ClaimStatus"] = "{0} <color=#e67e22>{1}</color> raportează că nu ai votat încă pe <color=#e67e22>{2}</color>. Votează acum!",
                ["PleaseWait"] = "{0} Se verifică toate API-urile site-urilor de vot... Te rugăm să ai răbdare, acest proces poate dura ceva timp...",
                ["VoteList"] = "{0} Poți vota pentru server-ul nostru accesând următoarele link-uri:",
                ["EarnRewardAutomatic"] = "Recompensa va fi revendicată automat cât timp ești online sau data viitoare când intri pe server!",
                ["ThankYou"] = "{0} Mulțumim pentru vot! Ai votat de <color=#e67e22>{1}</color> ori. Iată recompensa ta pentru asta: <color=#e67e22>{2}</color>",
                ["ThankYouNoReward"] = "{0} Mulțumim pentru vot! Votul tău pe <color=#e67e22>{2}</color> a fost înregistrat, iar acum ai <color=#e67e22>{1}</color> voturi. Nu este configurată nicio recompensă pentru acest prag.",
                ["NoRewards"] = "{0} Nu ai votat pentru <color=#e67e22>{1}</color> pe <color=#e67e22>{2}</color> încă! Scrie <color=#e67e22>/vote</color> pentru a începe!",
                ["GlobalChatAnnouncements"] = "{0} <color=#e67e22>{1}</color> a votat de <color=#e67e22>{2}</color> ori și tocmai a primit recompensele. Află unde poți vota scriind <color=#e67e22>/vote</color>\nPentru a vedea lista de recompense disponibile, scrie <color=#e67e22>/rewardlist</color>",
                ["GlobalChatAnnouncementsNoReward"] = "{0} <color=#e67e22>{1}</color> a votat de <color=#e67e22>{2}</color> ori. Află unde poți vota scriind <color=#e67e22>/vote</color>.",
                ["AlreadyVoted"] = "{0} <color=#e67e22>{1}</color> raportează că ai votat deja! Poți vota din nou mai târziu.",
                ["DiscordWebhookMessage"] = "{0} a votat pentru {1} pe {2} și a primit recompense! Scrie /rewardlist în joc pentru a vedea ce poți obține când votezi pentru noi!",
                ["DiscordWebhookMessageNoReward"] = "{0} a votat pentru {1} pe {2}. Votul a fost înregistrat cu succes.",
                ["RewardsListHeader"] = "{0} Următoarele recompense sunt acordate pentru vot!",
                ["NoConfiguredRewards"] = "Momentan nu este configurată nicio recompensă pentru vot.",
                ["RewardDescriptionMissing"] = "Descriere neconfigurată",
                ["EveryVote"] = "Fiecare Vot: <color=#e67e22>{0}</color>",
                ["FirstVote"] = "Primul Vot: <color=#e67e22>{0}</color>",
                ["NumberVote"] = "Votul nr. {0}: <color=#e67e22>{1}</color>",
                ["VoteLink"] = "{0} ({1}): <color=#e67e22>{2}</color>",
                ["VoteLinkCustom"] = "{0}: <color=#e67e22>{1}</color>",
                ["ConsoleServerOnly"] = "Comanda '{0}' poate fi executată doar din consola serverului sau prin RCON.",
                ["ConsolePlayerNotFound"] = "Nu a fost găsit niciun jucător care să corespundă cu '{0}'.",
                ["ConsoleClearVoteUsage"] = "Utilizare: {0} <steamid|nume>",
                ["ConsoleClearVoteSuccess"] = "Numărul de voturi pentru {0} a fost resetat la 0.",
                ["ConsoleCheckVoteUsage"] = "Utilizare: {0} <steamid|nume>",
                ["ConsoleCheckVoteSuccess"] = "{0} are în total {1} vot(uri).",
                ["ConsoleSetVoteUsage"] = "Utilizare: {0} <steamid|nume> [număr voturi]",
                ["ConsoleInvalidVoteCount"] = "'{0}' nu este un număr valid de voturi. Introdu un număr întreg mai mare sau egal cu 0.",
                ["ConsoleSetVoteSuccess"] = "Numărul de voturi pentru {0} a fost setat la {1}.",
                ["ConsoleResetVoteDataUsage"] = "Utilizare: {0}",
                ["ConsoleResetVoteDataSuccess"] = "Datele de vot au fost resetate pentru {0} jucător(i)."
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

        private void LoadPendingRewardsData()
        {
            try
            {
                pendingRewardCommands = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, List<string>>>(PendingRewardsDataFileName) ??
                                        new Dictionary<string, List<string>>();
            }
            catch (Exception exception)
            {
                pendingRewardCommands = new Dictionary<string, List<string>>();
                ConsoleError($"Failed to load pending vote rewards data: {exception.Message}");
            }
        }

        private void SavePendingRewardsData()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(PendingRewardsDataFileName, pendingRewardCommands);
            }
            catch (Exception exception)
            {
                ConsoleError($"Failed to save pending vote rewards data: {exception.Message}");
            }
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

            return GetStoredVoteCount(steamID);
        }
    }
} 
