using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    [Info("Easy Vote Extended", "dFxPhoeniX&TimRS", "3.2.9")]
    [Description("The best Rust server voting system")]
    public class EasyVoteExtended : RustPlugin
    {
        private readonly HashSet<string> pendingClaims = new HashSet<string>();
        private readonly HashSet<string> pendingStatusChecks = new HashSet<string>();
        private readonly Dictionary<string, string> statusRequestTokens = new Dictionary<string, string>();
        private readonly HashSet<ulong> scheduledVoteChecks = new HashSet<ulong>();
        private readonly HashSet<string> reportedConfigurationWarnings = new HashSet<string>();
        private readonly Queue<Action> claimRequestQueue = new Queue<Action>();
        private readonly Queue<Action> voteRequestQueue = new Queue<Action>();
        private readonly Dictionary<string, float> commandCooldowns = new Dictionary<string, float>();
        private readonly HashSet<string> scheduledClaimRetries = new HashSet<string>();
        private Dictionary<string, List<string>> pendingRewardCommands = new Dictionary<string, List<string>>();
        private Dictionary<string, PendingClaimTransaction> pendingClaimTransactions = new Dictionary<string, PendingClaimTransaction>();

        private const int CurrentConfigurationVersion = 1;
        private const int DefaultAutomaticVoteCheckInterval = 300;
        private const int DefaultVoteFollowUpCheckDelay = 60;
        private const float VoteRequestSpacing = 0.2f;
        private const float ManualCommandCooldown = 10f;
        private const int VoteApiRequestTimeout = 15000;
        private const int MaximumClaimAttempts = 6;
        private const string PendingRewardsDataFileName = "EasyVoteExtended_PendingRewards";
        private const string PendingClaimsDataFileName = "EasyVoteExtended_PendingClaims";
        private const string PendingRewardTransactionPrefix = "__EVE_TX__:";
        private const string ClaimStateCreated = "Created";
        private const string ClaimStateSent = "Sent";
        private const string ClaimStateUncertain = "Uncertain";
        private const string ClaimStateConfirmed = "Confirmed";
        private const string ClaimStateManualReview = "ManualReview";

        private class PendingClaimTransaction
        {
            public string TransactionId;
            public ulong PlayerId;
            public string PlayerName;
            public string ServerName;
            public string Site;
            public bool NotifyPlayer;
            public int Attempts;
            public string State;
            public bool HadAmbiguousFailure;
            public int TargetVoteCount;
        }

        ////////////////////////////////////////////////////////////
        // Oxide Hooks
        ////////////////////////////////////////////////////////////

        private void Init()
        {
            // LoadConfig() normally validates the configuration before Init().
            // Keep this fallback only for unexpected load-order scenarios.
            if (_config == null)
            {
                _config = Config.ReadObject<PluginConfig>();
                EnsureConfigDefaults();
            }

            LoadMessages();
            LoadPendingRewardsData();
            LoadPendingClaimsData();
        }

        private void OnServerInitialized()
        {
            ConsoleLog("Easy Vote Extended has been initialized...");

            StartRequestQueueProcessor();
            StartAutomaticVoteChecks();
            RecoverPendingClaimTransactions();

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
            statusRequestTokens.Clear();
            scheduledVoteChecks.Clear();
            reportedConfigurationWarnings.Clear();
            claimRequestQueue.Clear();
            voteRequestQueue.Clear();
            commandCooldowns.Clear();
            scheduledClaimRetries.Clear();
            SavePendingRewardsData();
            SavePendingClaimsData();
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

            ulong connectedPlayerId = player.userID;
            timer.Once(2f, () =>
            {
                BasePlayer connectedPlayer = BasePlayer.FindByID(connectedPlayerId);
                if (connectedPlayer != null && connectedPlayer.IsConnected)
                {
                    DeliverPendingRewards(connectedPlayer);
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
            scheduledVoteChecks.Remove(player.userID);
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

        private void HandleClaimWebRequestCallback(int code, string response, string pendingClaimKey, string transactionId, string requestToken)
        {
            pendingClaims.Remove(requestToken);

            PendingClaimTransaction transaction;
            if (!TryGetActiveClaimTransaction(pendingClaimKey, transactionId, out transaction))
            {
                _Debug($"Ignoring a stale claim callback for transaction {transactionId}.");
                return;
            }

            string playerSteamId = transaction.PlayerId.ToString();
            BasePlayer player = FindPlayer(transaction.PlayerId);
            string resolvedPlayerName = GetResolvedPlayerName(player, transaction.PlayerName, playerSteamId);

            if (code != 200)
            {
                transaction.State = ClaimStateUncertain;
                transaction.HadAmbiguousFailure = true;
                SavePendingClaimsData();

                ConsoleError($"An error occurred while trying to claim the vote reward of the player {resolvedPlayerName}:{playerSteamId}");
                ConsoleWarn($"HTTP Code: {code} | Response: {response} | Server Name: {transaction.ServerName}");
                ConsoleWarn("The claim transaction was preserved and will be retried automatically.");
                ScheduleClaimRetry(pendingClaimKey);
                return;
            }

            response = response?.Trim();

            _Debug("------------------------------");
            _Debug("Method: HandleClaimWebRequestCallback");
            _Debug($"Site: {transaction.Site}");
            _Debug($"Code: {code}");
            _Debug($"Response: {response}");
            _Debug($"ServerName: {transaction.ServerName}");
            _Debug($"Player Name: {resolvedPlayerName}");
            _Debug($"Player SteamID: {playerSteamId}");
            _Debug("Web Request Type: Claim");

            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                _Debug($"Verbose Debug Enabled, Setting Response Code to: {_config.DebugSettings[ConfigDefaultKeys.ClaimAPIRepsonseCode]}");
                response = _config.DebugSettings[ConfigDefaultKeys.ClaimAPIRepsonseCode];
            }

            if (response == "1")
            {
                ConfirmClaimTransaction(pendingClaimKey, transaction);
                return;
            }

            if (response == "2")
            {
                if (transaction.HadAmbiguousFailure)
                {
                    // A previous request was sent but its response was lost or invalid.
                    // In the normal one-plugin workflow, "already claimed" now confirms
                    // that the preserved request reached the tracker.
                    ConfirmClaimTransaction(pendingClaimKey, transaction);
                    return;
                }

                RemovePendingClaimTransaction(pendingClaimKey);

                if (transaction.NotifyPlayer && player != null && player.IsConnected)
                {
                    player.ChatMessage(_lang("AlreadyVoted", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], transaction.Site));
                }

                return;
            }

            if (response == "0")
            {
                RemovePendingClaimTransaction(pendingClaimKey);

                if (transaction.NotifyPlayer && player != null && player.IsConnected)
                {
                    player.ChatMessage(_lang("ClaimStatus", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], transaction.Site, transaction.ServerName));
                }

                return;
            }

            transaction.State = ClaimStateUncertain;
            transaction.HadAmbiguousFailure = true;
            SavePendingClaimsData();

            ConsoleWarn($"Unexpected claim API response from {transaction.Site} for {resolvedPlayerName}/{playerSteamId} on {transaction.ServerName}: '{response ?? "null"}'. Expected 0, 1, or 2. The claim transaction will be retried.");
            ScheduleClaimRetry(pendingClaimKey);
        }

        private bool TryGetActiveClaimTransaction(string pendingClaimKey, string transactionId, out PendingClaimTransaction transaction)
        {
            transaction = null;

            return !string.IsNullOrEmpty(transactionId) &&
                   pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) &&
                   transaction != null &&
                   string.Equals(transaction.TransactionId, transactionId, StringComparison.Ordinal);
        }

        private void ConfirmClaimTransaction(string pendingClaimKey, PendingClaimTransaction transaction)
        {
            if (transaction == null ||
                !pendingClaimTransactions.ContainsKey(pendingClaimKey) ||
                !ReferenceEquals(pendingClaimTransactions[pendingClaimKey], transaction))
            {
                return;
            }

            if (transaction.TargetVoteCount <= 0)
            {
                transaction.TargetVoteCount = GetStoredVoteCount(transaction.PlayerId.ToString()) + 1;
            }

            transaction.State = ClaimStateConfirmed;
            SavePendingClaimsData();
            FinalizeConfirmedClaimTransaction(pendingClaimKey, transaction.TransactionId);
        }

        private void FinalizeConfirmedClaimTransaction(string pendingClaimKey, string transactionId)
        {
            PendingClaimTransaction transaction;
            if (!TryGetActiveClaimTransaction(pendingClaimKey, transactionId, out transaction) ||
                !string.Equals(transaction.State, ClaimStateConfirmed, StringComparison.Ordinal))
            {
                return;
            }

            string playerSteamId = transaction.PlayerId.ToString();
            BasePlayer player = FindPlayer(transaction.PlayerId);
            string resolvedPlayerName = GetResolvedPlayerName(player, transaction.PlayerName, playerSteamId);
            int targetVoteCount = Math.Max(1, transaction.TargetVoteCount);
            int currentVoteCount = GetStoredVoteCount(playerSteamId);

            if (currentVoteCount < targetVoteCount)
            {
                DataFile[playerSteamId] = targetVoteCount;
                SaveDataFile(DataFile);
            }

            bool cumulativeRewards = _config.PluginSettings[ConfigDefaultKeys.RewardIsCumulative].ToBool();
            List<string> rewardCommands = BuildRewardCommands(transaction.PlayerId, resolvedPlayerName, targetVoteCount, cumulativeRewards);
            bool rewardConfigured = rewardCommands.Count > 0;

            if (rewardConfigured &&
                !QueuePendingRewardTransaction(playerSteamId, transaction.TransactionId, rewardCommands))
            {
                ConsoleError($"Failed to persist reward commands for claim transaction {transaction.TransactionId}. Local completion will be retried.");
                ScheduleClaimRetry(pendingClaimKey);
                return;
            }

            // Removing the claim transaction only after the vote count and reward
            // queue are persisted makes local completion idempotent after reloads.
            RemovePendingClaimTransaction(pendingClaimKey);

            bool rewardDeliveredImmediately = rewardConfigured &&
                                             player != null &&
                                             player.IsConnected &&
                                             !player.IsSleeping();

            if (rewardDeliveredImmediately)
            {
                DeliverPendingRewards(player, false);
            }

            string displayedVoteCount = GetStoredVoteCount(playerSteamId).ToString();

            if (player != null && player.IsConnected)
            {
                string messageKey = rewardConfigured
                    ? (rewardDeliveredImmediately ? "ThankYou" : "ThankYouPending")
                    : "ThankYouNoReward";

                player.ChatMessage(_lang(messageKey, player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], displayedVoteCount, transaction.Site));
            }

            if (_config.Discord[ConfigDefaultKeys.DiscordEnabled].ToBool())
            {
                string discordMessageKey = rewardConfigured
                    ? (rewardDeliveredImmediately ? "DiscordWebhookMessage" : "DiscordWebhookMessagePending")
                    : "DiscordWebhookMessageNoReward";

                ServerMgr.Instance.StartCoroutine(DiscordSendMessage(_lang(discordMessageKey, null, resolvedPlayerName, transaction.ServerName, transaction.Site)));
            }

            if (_config.NotificationSettings[ConfigDefaultKeys.GlobalChatAnnouncements].ToBool())
            {
                string globalMessageKey = rewardConfigured
                    ? (rewardDeliveredImmediately ? "GlobalChatAnnouncements" : "GlobalChatAnnouncementsPending")
                    : "GlobalChatAnnouncementsNoReward";

                foreach (BasePlayer recipient in BasePlayer.activePlayerList)
                {
                    if (recipient == null || !recipient.IsConnected)
                    {
                        continue;
                    }

                    recipient.ChatMessage(_lang(globalMessageKey, recipient.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], resolvedPlayerName, displayedVoteCount));
                }
            }
        }

        private void HandleStatusWebRequestCallback(int code, string response, ulong playerId, string playerName, string statusUrl, string serverName, string site, bool notifyPlayer, string pendingStatusKey, string statusRequestToken)
        {
            if (!TryConsumeStatusRequest(pendingStatusKey, statusRequestToken))
            {
                _Debug($"Ignoring a stale status callback for {playerName}/{playerId} on {site} ({serverName}).");
                return;
            }

            string playerSteamId = playerId.ToString();
            BasePlayer player = FindPlayer(playerId);
            string resolvedPlayerName = GetResolvedPlayerName(player, playerName, playerSteamId);

            if (code != 200)
            {
                ConsoleError($"An error occurred while trying to check the vote status of the player {resolvedPlayerName}:{playerSteamId}");
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
            _Debug($"Player Name: {resolvedPlayerName}");
            _Debug($"Player SteamID: {playerSteamId}");
            _Debug("Web Request Type: Status/Check");

            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                _Debug($"Verbose Debug Enabled, Setting Response Code to: {_config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode]}");
                response = _config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode];
            }

            string pendingClaimKey = GetPendingClaimKey(playerId, serverName, site);
            PendingClaimTransaction existingTransaction;
            pendingClaimTransactions.TryGetValue(pendingClaimKey, out existingTransaction);

            if (response == "1")
            {
                EnqueueClaimRequest(playerId, resolvedPlayerName, serverName, site, notifyPlayer);
                return;
            }

            if (response == "0")
            {
                if (existingTransaction != null)
                {
                    RemovePendingClaimTransaction(pendingClaimKey);
                }

                if (notifyPlayer && player != null && player.IsConnected)
                {
                    player.ChatMessage(_lang("NoRewards", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], serverName, site));
                }

                return;
            }

            if (response == "2")
            {
                if (existingTransaction != null && existingTransaction.HadAmbiguousFailure)
                {
                    ConfirmClaimTransaction(pendingClaimKey, existingTransaction);
                    return;
                }

                if (existingTransaction != null)
                {
                    RemovePendingClaimTransaction(pendingClaimKey);
                }

                if (notifyPlayer && player != null && player.IsConnected)
                {
                    player.ChatMessage(_lang("AlreadyVoted", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site));
                }

                return;
            }

            ConsoleWarn($"Unexpected status API response from {site} for {resolvedPlayerName}/{playerSteamId} on {serverName}: '{response ?? "null"}'. Expected 0, 1, or 2.");
        }

        private bool IsActiveStatusRequest(string pendingStatusKey, string statusRequestToken)
        {
            string activeToken;
            return !string.IsNullOrEmpty(statusRequestToken) &&
                   statusRequestTokens.TryGetValue(pendingStatusKey, out activeToken) &&
                   string.Equals(activeToken, statusRequestToken, StringComparison.Ordinal);
        }

        private bool TryConsumeStatusRequest(string pendingStatusKey, string statusRequestToken)
        {
            if (!IsActiveStatusRequest(pendingStatusKey, statusRequestToken))
            {
                return false;
            }

            statusRequestTokens.Remove(pendingStatusKey);
            pendingStatusChecks.Remove(pendingStatusKey);
            return true;
        }

        private void CancelStatusRequest(string pendingStatusKey, string statusRequestToken)
        {
            if (!IsActiveStatusRequest(pendingStatusKey, statusRequestToken))
            {
                return;
            }

            statusRequestTokens.Remove(pendingStatusKey);
            pendingStatusChecks.Remove(pendingStatusKey);
        }

        private void CancelStatusRequestsForPlayer(string steamId)
        {
            if (string.IsNullOrEmpty(steamId))
            {
                return;
            }

            foreach (string pendingStatusKey in statusRequestTokens.Keys
                .Where(key => key.StartsWith(steamId + ":", StringComparison.Ordinal))
                .ToList())
            {
                statusRequestTokens.Remove(pendingStatusKey);
                pendingStatusChecks.Remove(pendingStatusKey);
            }
        }

        private void EnqueueClaimRequest(ulong playerId, string playerName, string serverName, string site, bool notifyPlayer)
        {
            string pendingClaimKey = GetPendingClaimKey(playerId, serverName, site);
            PendingClaimTransaction transaction;

            if (!pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) || transaction == null)
            {
                transaction = new PendingClaimTransaction
                {
                    TransactionId = Guid.NewGuid().ToString("N"),
                    PlayerId = playerId,
                    PlayerName = playerName,
                    ServerName = serverName,
                    Site = site,
                    NotifyPlayer = notifyPlayer,
                    Attempts = 0,
                    State = ClaimStateCreated,
                    HadAmbiguousFailure = false,
                    TargetVoteCount = 0
                };

                pendingClaimTransactions[pendingClaimKey] = transaction;
            }
            else
            {
                transaction.PlayerName = playerName;
                transaction.NotifyPlayer |= notifyPlayer;

                if (string.Equals(transaction.State, ClaimStateManualReview, StringComparison.Ordinal))
                {
                    transaction.Attempts = 0;
                    transaction.State = ClaimStateCreated;
                    transaction.HadAmbiguousFailure = false;
                }
            }

            SavePendingClaimsData();
            QueuePendingClaimTransaction(pendingClaimKey);
        }

        private void QueuePendingClaimTransaction(string pendingClaimKey)
        {
            PendingClaimTransaction transaction;
            if (!pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) || transaction == null)
            {
                pendingClaimTransactions.Remove(pendingClaimKey);
                SavePendingClaimsData();
                return;
            }

            if (string.IsNullOrEmpty(transaction.TransactionId))
            {
                transaction.TransactionId = Guid.NewGuid().ToString("N");
                SavePendingClaimsData();
            }

            if (string.Equals(transaction.State, ClaimStateConfirmed, StringComparison.Ordinal))
            {
                FinalizeConfirmedClaimTransaction(pendingClaimKey, transaction.TransactionId);
                return;
            }

            if (string.Equals(transaction.State, ClaimStateManualReview, StringComparison.Ordinal))
            {
                return;
            }

            if (transaction.Attempts >= MaximumClaimAttempts)
            {
                transaction.State = ClaimStateManualReview;
                SavePendingClaimsData();
                ConsoleError($"Claim transaction {transaction.TransactionId} for {transaction.PlayerName}/{transaction.PlayerId} on {transaction.Site} reached the retry limit and requires manual review.");
                return;
            }

            string claimUrl;
            if (!TryBuildClaimUrl(transaction, out claimUrl))
            {
                transaction.State = ClaimStateManualReview;
                SavePendingClaimsData();
                ConsoleError($"Unable to rebuild the claim URL for {transaction.PlayerName}/{transaction.PlayerId} on {transaction.Site} ({transaction.ServerName}). The transaction requires manual review.");
                return;
            }

            string requestToken = $"{pendingClaimKey}:{transaction.TransactionId}";
            if (!pendingClaims.Add(requestToken))
            {
                _Debug($"A claim request is already pending for {transaction.PlayerName}/{transaction.PlayerId} on {transaction.Site} ({transaction.ServerName}).");
                return;
            }

            scheduledClaimRetries.Remove(pendingClaimKey);
            _Debug($"Automatic claim URL: {MaskApiUrl(claimUrl)}");

            QueueVoteRequest(() =>
            {
                PendingClaimTransaction currentTransaction;
                if (!TryGetActiveClaimTransaction(pendingClaimKey, transaction.TransactionId, out currentTransaction))
                {
                    pendingClaims.Remove(requestToken);
                    return;
                }

                currentTransaction.Attempts++;
                currentTransaction.State = ClaimStateSent;
                SavePendingClaimsData();

                try
                {
                    webrequest.Enqueue(
                        claimUrl,
                        null,
                        (code, response) => HandleClaimWebRequestCallback(code, response, pendingClaimKey, currentTransaction.TransactionId, requestToken),
                        this,
                        RequestMethod.GET,
                        null,
                        VoteApiRequestTimeout);
                }
                catch (Exception exception)
                {
                    pendingClaims.Remove(requestToken);

                    if (TryGetActiveClaimTransaction(pendingClaimKey, currentTransaction.TransactionId, out currentTransaction))
                    {
                        currentTransaction.State = ClaimStateUncertain;
                        currentTransaction.HadAmbiguousFailure = true;
                        SavePendingClaimsData();
                    }

                    ConsoleError($"Failed to enqueue the claim request for {transaction.PlayerName}/{transaction.PlayerId} on {transaction.Site}: {exception.Message}");
                    ScheduleClaimRetry(pendingClaimKey);
                }
            }, true);
        }

        private bool TryBuildClaimUrl(PendingClaimTransaction transaction, out string claimUrl)
        {
            claimUrl = null;

            if (transaction == null || _config.Servers == null)
            {
                return false;
            }

            KeyValuePair<string, Dictionary<string, string>> server = _config.Servers.FirstOrDefault(entry =>
                entry.Key.Equals(transaction.ServerName, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(server.Key) || server.Value == null)
            {
                return false;
            }

            KeyValuePair<string, string> configuredVoteSite = server.Value.FirstOrDefault(entry =>
                entry.Key.Equals(transaction.Site, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(configuredVoteSite.Key))
            {
                return false;
            }

            string configuredSiteName;
            Dictionary<string, string> apiConfiguration;
            string serverId;
            string serverKey;

            if (!TryGetVoteSiteConfiguration(configuredVoteSite.Key, out configuredSiteName, out apiConfiguration) ||
                !TryParseServerCredentials(configuredVoteSite.Value, out serverId, out serverKey))
            {
                return false;
            }

            try
            {
                claimUrl = FormatApiUrl(
                    apiConfiguration,
                    ConfigDefaultKeys.apiClaim,
                    transaction.PlayerId,
                    transaction.PlayerName,
                    serverId,
                    serverKey);
                return true;
            }
            catch (Exception exception)
            {
                ConsoleError($"Failed to format the recovered claim URL for {configuredSiteName} on {server.Key}: {exception.Message}");
                return false;
            }
        }

        private void ScheduleClaimRetry(string pendingClaimKey)
        {
            PendingClaimTransaction transaction;
            if (!pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) ||
                transaction == null ||
                string.Equals(transaction.State, ClaimStateManualReview, StringComparison.Ordinal) ||
                !scheduledClaimRetries.Add(pendingClaimKey))
            {
                return;
            }

            if (!string.Equals(transaction.State, ClaimStateConfirmed, StringComparison.Ordinal) &&
                transaction.Attempts >= MaximumClaimAttempts)
            {
                scheduledClaimRetries.Remove(pendingClaimKey);
                transaction.State = ClaimStateManualReview;
                SavePendingClaimsData();
                ConsoleError($"Claim transaction {transaction.TransactionId} for {transaction.PlayerName}/{transaction.PlayerId} on {transaction.Site} reached the retry limit and requires manual review.");
                return;
            }

            float retryDelay = string.Equals(transaction.State, ClaimStateConfirmed, StringComparison.Ordinal)
                ? 10f
                : (transaction.Attempts <= 3
                    ? Math.Max(10f, transaction.Attempts * 10f)
                    : 300f);

            _Debug($"Claim retry for {transaction.PlayerName}/{transaction.PlayerId} on {transaction.Site} scheduled in {retryDelay:0} seconds.");

            timer.Once(retryDelay, () =>
            {
                scheduledClaimRetries.Remove(pendingClaimKey);

                if (pendingClaimTransactions.ContainsKey(pendingClaimKey))
                {
                    QueuePendingClaimTransaction(pendingClaimKey);
                }
            });
        }

        private void RecoverPendingClaimTransactions()
        {
            bool changed = false;

            foreach (KeyValuePair<string, PendingClaimTransaction> entry in pendingClaimTransactions.ToList())
            {
                PendingClaimTransaction transaction = entry.Value;
                if (transaction == null)
                {
                    pendingClaimTransactions.Remove(entry.Key);
                    changed = true;
                    continue;
                }

                if (string.IsNullOrEmpty(transaction.TransactionId))
                {
                    transaction.TransactionId = Guid.NewGuid().ToString("N");
                    changed = true;
                }

                if (string.IsNullOrEmpty(transaction.State))
                {
                    transaction.State = transaction.Attempts > 0 ? ClaimStateUncertain : ClaimStateCreated;
                    transaction.HadAmbiguousFailure = transaction.Attempts > 0;
                    changed = true;
                }
                else if (string.Equals(transaction.State, ClaimStateSent, StringComparison.Ordinal))
                {
                    // The server stopped while a request was in flight, so the
                    // result is ambiguous and a later response 2 may be ours.
                    transaction.State = ClaimStateUncertain;
                    transaction.HadAmbiguousFailure = true;
                    changed = true;
                }
            }

            if (changed)
            {
                SavePendingClaimsData();
            }

            foreach (KeyValuePair<string, PendingClaimTransaction> entry in pendingClaimTransactions.ToList())
            {
                if (entry.Value == null || string.Equals(entry.Value.State, ClaimStateManualReview, StringComparison.Ordinal))
                {
                    continue;
                }

                QueuePendingClaimTransaction(entry.Key);
            }
        }

        private string GetPendingClaimKey(ulong playerId, string serverName, string site)
        {
            return $"{playerId}:{serverName}:{site}";
        }

        private void RemovePendingClaimTransaction(string pendingClaimKey)
        {
            scheduledClaimRetries.Remove(pendingClaimKey);

            PendingClaimTransaction transaction;
            if (pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) && transaction != null)
            {
                pendingClaims.Remove($"{pendingClaimKey}:{transaction.TransactionId}");
            }

            if (pendingClaimTransactions.Remove(pendingClaimKey))
            {
                SavePendingClaimsData();
            }
        }

        private BasePlayer FindPlayer(ulong playerId)
        {
            return BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
        }

        private string GetResolvedPlayerName(BasePlayer player, string fallbackName, string steamId)
        {
            if (player != null && !string.IsNullOrWhiteSpace(player.displayName))
            {
                return player.displayName;
            }

            return string.IsNullOrWhiteSpace(fallbackName) ? steamId : fallbackName;
        }

        private string FormatApiUrl(Dictionary<string, string> apiConfiguration, string apiKey, BasePlayer player, string serverId, string serverKey)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            return FormatApiUrl(apiConfiguration, apiKey, player.userID, player.displayName, serverId, serverKey);
        }

        private string FormatApiUrl(Dictionary<string, string> apiConfiguration, string apiKey, ulong playerId, string playerName, string serverId, string serverKey)
        {
            string apiLink = apiConfiguration[apiKey];
            bool usernameApiEnabled = apiConfiguration[ConfigDefaultKeys.apiUsername].ToBool();
            string encodedServerKey = Uri.EscapeDataString(serverKey);
            string encodedServerId = Uri.EscapeDataString(serverId);
            string encodedPlayerIdentifier = usernameApiEnabled
                ? Uri.EscapeDataString(playerName ?? string.Empty)
                : playerId.ToString();

            // Extra format arguments are ignored when the URL does not use them.
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

            return Regex.Replace(
                url,
                @"([?&][^=&]*(?:key|token|secret|auth|password)[^=&]*=)[^&]*",
                "$1********",
                RegexOptions.IgnoreCase);
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

        private List<string> BuildRewardCommands(ulong playerId, string playerName, int playerVoteCount, bool cumulativeRewards)
        {
            List<string> commands = new List<string>();
            string steamId = playerId.ToString();

            AddRewardCommands(commands, "@", steamId, playerName);

            if (playerVoteCount == 1)
            {
                AddRewardCommands(commands, "first", steamId, playerName);
            }

            foreach (KeyValuePair<string, List<string>> reward in _config.Rewards)
            {
                int requiredVoteCount;
                if (!TryGetNumericRewardVoteCount(reward.Key, out requiredVoteCount))
                {
                    continue;
                }

                if (cumulativeRewards ? requiredVoteCount <= playerVoteCount : requiredVoteCount == playerVoteCount)
                {
                    AddRewardCommands(commands, reward.Key, steamId, playerName);
                }
            }

            return commands;
        }

        private void AddRewardCommands(List<string> destination, string rewardKey, string steamId, string playerName)
        {
            List<string> configuredCommands;
            if (destination == null ||
                !_config.Rewards.TryGetValue(rewardKey, out configuredCommands) ||
                configuredCommands == null)
            {
                return;
            }

            foreach (string configuredCommand in configuredCommands)
            {
                if (string.IsNullOrWhiteSpace(configuredCommand))
                {
                    continue;
                }

                string command = ParseRewardCommand(steamId, playerName, configuredCommand);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    destination.Add(command);
                }
            }
        }

        private bool QueuePendingRewardTransaction(string steamId, string transactionId, List<string> commands)
        {
            if (string.IsNullOrWhiteSpace(steamId) ||
                string.IsNullOrWhiteSpace(transactionId) ||
                commands == null ||
                commands.Count == 0)
            {
                return false;
            }

            List<string> pendingCommands;
            if (!pendingRewardCommands.TryGetValue(steamId, out pendingCommands) || pendingCommands == null)
            {
                pendingCommands = new List<string>();
                pendingRewardCommands[steamId] = pendingCommands;
            }

            string marker = GetPendingRewardTransactionMarker(transactionId);
            if (pendingCommands.Contains(marker))
            {
                return true;
            }

            int originalCount = pendingCommands.Count;
            pendingCommands.Add(marker);
            pendingCommands.AddRange(commands.Where(command => !string.IsNullOrWhiteSpace(command)));

            if (!SavePendingRewardsData())
            {
                pendingCommands.RemoveRange(originalCount, pendingCommands.Count - originalCount);

                if (pendingCommands.Count == 0)
                {
                    pendingRewardCommands.Remove(steamId);
                }

                return false;
            }

            _Debug($"Queued {commands.Count} pending reward command(s) for {steamId}, transaction {transactionId}.");
            return true;
        }

        private string GetPendingRewardTransactionMarker(string transactionId)
        {
            return PendingRewardTransactionPrefix + transactionId;
        }

        private bool IsPendingRewardTransactionMarker(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.StartsWith(PendingRewardTransactionPrefix, StringComparison.Ordinal);
        }

        private bool TryGetNumericRewardVoteCount(string rewardKey, out int requiredVoteCount)
        {
            return int.TryParse(rewardKey, out requiredVoteCount) && requiredVoteCount > 0;
        }

        private bool HasRewardCommands(List<string> rewardCommands)
        {
            return rewardCommands != null && rewardCommands.Any(command => !string.IsNullOrWhiteSpace(command));
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

        private bool DeliverPendingRewards(BasePlayer player, bool notifyPlayer = true)
        {
            if (player == null || !player.IsConnected || player.IsSleeping())
            {
                return false;
            }

            List<string> commands;
            if (!pendingRewardCommands.TryGetValue(player.UserIDString, out commands) || commands == null || commands.Count == 0)
            {
                return false;
            }

            bool deliveredCommand = false;

            while (pendingRewardCommands.TryGetValue(player.UserIDString, out commands) &&
                   commands != null &&
                   commands.Count > 0)
            {
                string command = commands[0];
                commands.RemoveAt(0);

                bool removedPlayerEntry = commands.Count == 0;
                if (removedPlayerEntry)
                {
                    pendingRewardCommands.Remove(player.UserIDString);
                }

                // Persist removal before execution. This gives at-most-once delivery
                // across crashes and prevents a command from being replayed after it
                // has already been dispatched to the server console.
                if (!SavePendingRewardsData())
                {
                    if (removedPlayerEntry)
                    {
                        pendingRewardCommands[player.UserIDString] = commands;
                    }

                    commands.Insert(0, command);
                    return deliveredCommand;
                }

                if (IsPendingRewardTransactionMarker(command))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                _Debug($"Delivering pending reward command to {player.displayName}/{player.UserIDString}: {command}");
                rust.RunServerCommand(command);
                deliveredCommand = true;
            }

            if (deliveredCommand && notifyPlayer)
            {
                player.ChatMessage(_lang("PendingRewardsDelivered", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
            }

            return deliveredCommand;
        }

        private string ParseRewardCommand(string steamId, string playerName, string command)
        {
            string safePlayerName = string.IsNullOrWhiteSpace(playerName)
                ? steamId
                : playerName
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Replace(";", string.Empty)
                    .Replace("\"", "'");

            return command
                .Replace("{playerid}", steamId)
                .Replace("{playername}", safePlayerName);
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

            pendingStatusChecks.Clear();
            statusRequestTokens.Clear();
            voteRequestQueue.Clear();

            if (pendingRewardCommands.Count > 0)
            {
                pendingRewardCommands.Clear();
                SavePendingRewardsData();
                _Debug("All pending vote rewards have been cleared together with the vote data.");
            }

            if (pendingClaimTransactions.Count > 0)
            {
                pendingClaimTransactions.Clear();
                pendingClaims.Clear();
                scheduledClaimRetries.Clear();
                SavePendingClaimsData();
                _Debug("All pending vote claim transactions have been cleared together with the vote data.");
            }
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

            bool waitMessageSent = false;

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

                    ulong requestPlayerId = player.userID;
                    string requestPlayerName = player.displayName;
                    string requestPlayerSteamId = player.UserIDString;
                    string requestServerName = server.Key;
                    string requestSiteName = configuredSiteName;
                    string statusUrl = formattedStatusUrl;
                    string claimUrl = formattedClaimUrl;
                    bool requestNotifyPlayer = notifyPlayer;
                    string pendingStatusKey = $"{requestPlayerSteamId}:{requestServerName}:{requestSiteName}";
                    string pendingClaimKey = GetPendingClaimKey(requestPlayerId, requestServerName, requestSiteName);
                    PendingClaimTransaction existingClaimTransaction;

                    if (pendingClaimTransactions.TryGetValue(pendingClaimKey, out existingClaimTransaction) &&
                        existingClaimTransaction != null)
                    {
                        if (string.Equals(existingClaimTransaction.State, ClaimStateManualReview, StringComparison.Ordinal))
                        {
                            WarnConfigurationOnce(
                                $"manual-claim-review:{pendingClaimKey}",
                                $"Claim transaction {existingClaimTransaction.TransactionId} for {requestPlayerName}/{requestPlayerSteamId} on {requestSiteName} reached the retry limit. It was released so normal status checks can continue.");
                            RemovePendingClaimTransaction(pendingClaimKey);
                        }
                        else
                        {
                            if (string.Equals(existingClaimTransaction.State, ClaimStateConfirmed, StringComparison.Ordinal))
                            {
                                FinalizeConfirmedClaimTransaction(pendingClaimKey, existingClaimTransaction.TransactionId);
                            }
                            else
                            {
                                QueuePendingClaimTransaction(pendingClaimKey);
                            }

                            _Debug($"Skipping a duplicate status request because a claim transaction already exists for {requestPlayerName}/{requestPlayerSteamId} on {requestSiteName} ({requestServerName}).");
                            continue;
                        }
                    }

                    if (!pendingStatusChecks.Add(pendingStatusKey))
                    {
                        _Debug($"A status request is already pending for {requestPlayerName}/{requestPlayerSteamId} on {requestSiteName} ({requestServerName}).");
                        continue;
                    }

                    string statusRequestToken = Guid.NewGuid().ToString("N");
                    statusRequestTokens[pendingStatusKey] = statusRequestToken;

                    if (!waitMessageSent && notifyPlayer && _config.NotificationSettings[ConfigDefaultKeys.PleaseWaitMessage].ToBool())
                    {
                        player.ChatMessage(_lang("PleaseWait", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
                        waitMessageSent = true;
                    }

                    _Debug($"Status URL: {MaskApiUrl(statusUrl)}");
                    _Debug($"Claim URL: {MaskApiUrl(claimUrl)}");

                    QueueVoteRequest(() =>
                    {
                        if (!IsActiveStatusRequest(pendingStatusKey, statusRequestToken))
                        {
                            return;
                        }

                        BasePlayer currentPlayer = BasePlayer.FindByID(requestPlayerId);
                        if (currentPlayer == null || !currentPlayer.IsConnected)
                        {
                            CancelStatusRequest(pendingStatusKey, statusRequestToken);
                            return;
                        }

                        try
                        {
                            webrequest.Enqueue(statusUrl, null,
                                (code, response) => HandleStatusWebRequestCallback(code, response, requestPlayerId, requestPlayerName, statusUrl, requestServerName, requestSiteName, requestNotifyPlayer, pendingStatusKey, statusRequestToken), this,
                                RequestMethod.GET, null, VoteApiRequestTimeout);
                        }
                        catch (Exception exception)
                        {
                            CancelStatusRequest(pendingStatusKey, statusRequestToken);
                            ConsoleError($"Failed to enqueue the status request for {requestPlayerName}/{requestPlayerSteamId} on {requestSiteName}: {exception.Message}");
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
            if (delay <= 0 || player == null)
            {
                return;
            }

            ulong playerId = player.userID;
            if (!scheduledVoteChecks.Add(playerId))
            {
                return;
            }

            timer.Once(delay, () =>
            {
                scheduledVoteChecks.Remove(playerId);

                BasePlayer currentPlayer = BasePlayer.FindByID(playerId);
                if (currentPlayer != null && currentPlayer.IsConnected)
                {
                    CheckVotingStatus(currentPlayer, false);
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

        protected void _Debug(string message)
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

            bool displayedVoteLink = false;

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
                    displayedVoteLink = true;
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
                    displayedVoteLink = true;
                }
            }

            if (!displayedVoteLink)
            {
                player.ChatMessage(_lang("NoVoteSitesConfigured", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
                return;
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
            string steamId;
            string targetLabel;

            if (!TryResolveConsoleTarget(targetInput, out steamId, out targetLabel))
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", targetInput);
                return;
            }

            DataFile[steamId] = 0;
            SaveDataFile(DataFile);
            CancelStatusRequestsForPlayer(steamId);

            if (pendingRewardCommands.Remove(steamId))
            {
                SavePendingRewardsData();
            }

            bool pendingClaimsRemoved = false;
            foreach (string pendingClaimKey in pendingClaimTransactions
                .Where(entry => entry.Value != null && entry.Value.PlayerId.ToString() == steamId)
                .Select(entry => entry.Key)
                .ToList())
            {
                PendingClaimTransaction transaction = pendingClaimTransactions[pendingClaimKey];
                pendingClaimTransactions.Remove(pendingClaimKey);

                if (transaction != null && !string.IsNullOrEmpty(transaction.TransactionId))
                {
                    pendingClaims.Remove($"{pendingClaimKey}:{transaction.TransactionId}");
                }

                scheduledClaimRetries.Remove(pendingClaimKey);
                pendingClaimsRemoved = true;
            }

            if (pendingClaimsRemoved)
            {
                SavePendingClaimsData();
            }

            ReplyConsoleLocalized(arg, "ConsoleClearVoteSuccess", targetLabel);
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
            string steamId;
            string targetLabel;

            if (!TryResolveConsoleTarget(targetInput, out steamId, out targetLabel))
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", targetInput);
                return;
            }

            ReplyConsoleLocalized(
                arg,
                "ConsoleCheckVoteSuccess",
                targetLabel,
                getPlayerVotes(steamId));
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
            string steamId;
            string targetLabel;

            if (!TryResolveConsoleTarget(targetInput, out steamId, out targetLabel))
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

            DataFile[steamId] = voteCount;
            SaveDataFile(DataFile);

            ReplyConsoleLocalized(
                arg,
                "ConsoleSetVoteSuccess",
                targetLabel,
                voteCount);
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

        private bool TryResolveConsoleTarget(string targetInput, out string steamId, out string targetLabel)
        {
            steamId = null;
            targetLabel = null;

            if (string.IsNullOrWhiteSpace(targetInput))
            {
                return false;
            }

            targetInput = targetInput.Trim();

            if (targetInput.Length >= 2 &&
                ((targetInput[0] == '"' && targetInput[targetInput.Length - 1] == '"') ||
                 (targetInput[0] == '\'' && targetInput[targetInput.Length - 1] == '\'')))
            {
                targetInput = targetInput.Substring(1, targetInput.Length - 2).Trim();
            }

            if (string.IsNullOrWhiteSpace(targetInput))
            {
                return false;
            }

            ulong playerId;
            if (ulong.TryParse(targetInput, out playerId))
            {
                if (!IsValidSteamId(playerId))
                {
                    return false;
                }
                steamId = playerId.ToString();

                BasePlayer knownPlayer =
                    BasePlayer.FindByID(playerId) ??
                    BasePlayer.FindSleeping(playerId);

                targetLabel = knownPlayer != null
                    ? FormatPlayerForConsole(knownPlayer)
                    : steamId;

                return true;
            }

            List<BasePlayer> exactMatches = BasePlayer.activePlayerList
                .Concat(BasePlayer.sleepingPlayerList)
                .Where(player =>
                    player != null &&
                    !string.IsNullOrEmpty(player.displayName) &&
                    player.displayName.Equals(
                        targetInput,
                        StringComparison.OrdinalIgnoreCase))
                .GroupBy(player => player.userID)
                .Select(group => group.First())
                .ToList();

            BasePlayer matchedPlayer =
                exactMatches.Count == 1 ? exactMatches[0] : null;

            if (matchedPlayer == null && exactMatches.Count == 0)
            {
                List<BasePlayer> partialMatches = BasePlayer.activePlayerList
                    .Concat(BasePlayer.sleepingPlayerList)
                    .Where(player =>
                        player != null &&
                        !string.IsNullOrEmpty(player.displayName) &&
                        player.displayName.IndexOf(
                            targetInput,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    .GroupBy(player => player.userID)
                    .Select(group => group.First())
                    .Take(2)
                    .ToList();

                if (partialMatches.Count == 1)
                {
                    matchedPlayer = partialMatches[0];
                }
            }

            if (matchedPlayer == null)
            {
                return false;
            }

            steamId = matchedPlayer.UserIDString;
            targetLabel = FormatPlayerForConsole(matchedPlayer);
            return true;
        }

        private bool IsValidSteamId(ulong playerId)
        {
            return playerId >= 76561197960265728UL && playerId.ToString().Length == 17;
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
            string message = _langConsole(key, null, args);

            if (arg != null && arg.Connection != null)
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

            player.SendConsoleCommand("echo", _langConsole(key, player.UserIDString, args));
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
                string formattedMessage = string.Format(message, args);

                // Most chat messages use the first placeholder for the configured prefix.
                // If the prefix is empty, remove the remaining leading whitespace without
                // changing messages that use a non-empty prefix or other first argument.
                if (args != null &&
                    args.Length > 0 &&
                    string.IsNullOrWhiteSpace(args[0]?.ToString()))
                {
                    return formattedMessage.TrimStart();
                }

                return formattedMessage;
            }
            catch (FormatException)
            {
                ConsoleWarn($"Language message '{key}' contains invalid formatting for target '{(id == null ? "server default" : id)}'.");
                return message;
            }
        }

        private string _langConsole(string key, string id = null, params object[] args)
        {
            return PrepareConsoleMessage(_lang(key, id, args));
        }

        private string PrepareConsoleMessage(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return source;
            }

            source = Regex.Replace(source, @"</?(color|size|b|i|material|alpha)(=[^>]+)?>", string.Empty, RegexOptions.IgnoreCase);
            return source.Replace('<', '‹').Replace('>', '›');
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
                { ConfigDefaultKeys.Prefix, "<color=#e67e22>[EasyVote]</color>" }
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
                ["ThankYouPending"] = "{0} Thank you for voting! You have voted <color=#e67e22>{1}</color> time(s). Your reward for <color=#e67e22>{2}</color> is pending and will be delivered when you are fully connected.",
                ["DiscordWebhookMessagePending"] = "{0} has voted for {1} on {2}. The reward is pending and will be delivered when the player reconnects or wakes up.",
                ["GlobalChatAnnouncementsPending"] = "{0} <color=#e67e22>{1}</color> has voted <color=#e67e22>{2}</color> time(s). Their reward is pending and will be delivered when they are fully connected.",
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
                ["NoVoteSitesConfigured"] = "{0} No voting sites are currently configured.",
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
                ["ThankYouPending"] = "{0} Mulțumim pentru vot! Ai votat de <color=#e67e22>{1}</color> ori. Recompensa pentru <color=#e67e22>{2}</color> este în așteptare și va fi acordată după ce ești conectat complet.",
                ["DiscordWebhookMessagePending"] = "{0} a votat pentru {1} pe {2}. Recompensa este în așteptare și va fi acordată atunci când jucătorul se reconectează sau se trezește.",
                ["GlobalChatAnnouncementsPending"] = "{0} <color=#e67e22>{1}</color> a votat de <color=#e67e22>{2}</color> ori. Recompensa este în așteptare și va fi acordată când jucătorul este conectat complet.",
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
                ["NoVoteSitesConfigured"] = "{0} Momentan nu este configurat niciun site de vot.",
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

        private bool SavePendingRewardsData()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(PendingRewardsDataFileName, pendingRewardCommands);
                return true;
            }
            catch (Exception exception)
            {
                ConsoleError($"Failed to save pending vote rewards data: {exception.Message}");
                return false;
            }
        }

        private void LoadPendingClaimsData()
        {
            try
            {
                pendingClaimTransactions = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, PendingClaimTransaction>>(PendingClaimsDataFileName) ??
                                           new Dictionary<string, PendingClaimTransaction>();
            }
            catch (Exception exception)
            {
                pendingClaimTransactions = new Dictionary<string, PendingClaimTransaction>();
                ConsoleWarn($"Failed to load pending claim transactions: {exception.Message}");
            }
        }

        private void SavePendingClaimsData()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(PendingClaimsDataFileName, pendingClaimTransactions);
            }
            catch (Exception exception)
            {
                ConsoleError($"Failed to save pending vote claim data: {exception.Message}");
            }
        }

        ////////////////////////////////////////////////////////////
        // Plugin Hooks
        ////////////////////////////////////////////////////////////

        [HookMethod(nameof(getPlayerVotes))]
        public int getPlayerVotes(string steamID)
        {
            return GetStoredVoteCount(steamID);
        }
    }
} 
