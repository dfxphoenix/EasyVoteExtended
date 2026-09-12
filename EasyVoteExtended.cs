using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Facepunch.Extend;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Easy Vote Extended", "dFxPhoeniX & TimRS", "3.3.2")]
    [Description("The best Rust server voting system")]
    public class EasyVoteExtended : RustPlugin
    {
        ////////////////////////////////////////////////////////////
        // Runtime State
        ////////////////////////////////////////////////////////////

        private readonly HashSet<string> pendingStatusChecks = new HashSet<string>();
        private readonly Dictionary<string, string> statusRequestTokens = new Dictionary<string, string>();
        private readonly Dictionary<ulong, string> scheduledVoteChecks = new Dictionary<ulong, string>();
        private readonly HashSet<string> reportedConfigurationWarnings = new HashSet<string>();
        private readonly Queue<Action> claimRequestQueue = new Queue<Action>();
        private readonly Queue<Action> voteRequestQueue = new Queue<Action>();
        private readonly Dictionary<string, float> commandCooldowns = new Dictionary<string, float>();
        private readonly Dictionary<string, string> scheduledClaimRetries = new Dictionary<string, string>();
        private Dictionary<string, List<string>> pendingRewardCommands = new Dictionary<string, List<string>>();
        private Dictionary<string, PendingClaimTransaction> pendingClaimTransactions = new Dictionary<string, PendingClaimTransaction>();
        private readonly Dictionary<string, string> activeClaimRequests = new Dictionary<string, string>();
        private readonly HashSet<ulong> finalizingPlayers = new HashSet<ulong>();
        private readonly HashSet<ulong> deliveringPlayers = new HashSet<ulong>();
        private readonly Dictionary<string, long> storageWarningTimes = new Dictionary<string, long>();
        private readonly Queue<DiscordNotification> discordQueue = new Queue<DiscordNotification>();

        private int runtimeGeneration;
        private bool configurationLoaded;
        private bool dataLoaded;
        private bool unloading;
        private bool resetInProgress;
        private bool wipeResetRequested;
        private bool debugEnabled;
        private bool claimsDirty;

        private VoteResetJournal pendingResetJournal;
        private string discordRequestToken;
        private long discordNextAttemptUtcTicks;
        private int consecutiveClaimRequests;

        ////////////////////////////////////////////////////////////
        // Constants
        ////////////////////////////////////////////////////////////

        private const int CurrentConfigurationVersion = 1;
        private const int DefaultAutomaticVoteCheckInterval = 300;
        private const int DefaultVoteFollowUpCheckDelay = 60;
        private const float VoteRequestSpacing = 0.2f;
        private const float ManualCommandCooldown = 10f;
        private const float VoteApiRequestTimeout = 15f;
        private const int MaximumClaimAttempts = 6;
        private const string PendingRewardsDataFileName = "EasyVoteExtended_PendingRewards";
        private const string PendingClaimsDataFileName = "EasyVoteExtended_PendingClaims";
        private const string PendingRewardTransactionPrefix = "__EVE_TX__:";
        private const string ClaimStateCreated = "Created";
        private const string ClaimStateSent = "Sent";
        private const string ClaimStateUncertain = "Uncertain";
        private const string ClaimStateConfirmed = "Confirmed";
        private const string ClaimStateManualReview = "ManualReview";
        private const int MaximumCommandsPerDelivery = 32;
        private const float LocalProcessingInterval = 5f;
        private const string ClaimStateClosed = "Closed";
        private const string ResetJournalDataFileName = "EasyVoteExtended_ResetJournal";

        ////////////////////////////////////////////////////////////
        // Models
        ////////////////////////////////////////////////////////////

        private enum RewardDeliveryResult
        {
            None,
            Pending,
            Partial,
            Dispatched,
            Failed
        }

        private class VoteResetJournal
        {
            public string OperationId;
            public string SteamId;
            public bool AllPlayers;
        }

        private class DiscordNotification
        {
            public string Url;
            public string Body;
            public int Attempts;
        }

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
            public int RecoveryAttempts;
            public long NextAttemptUtcTicks;
            public long CreatedUtcTicks;
            public bool NeedsStatusCheck;
            public List<string> RewardCommands;
            public string ReviewReason;
        }

        ////////////////////////////////////////////////////////////
        // Oxide Hooks
        ////////////////////////////////////////////////////////////

        private void Init()
        {
            if (!configurationLoaded || _config == null)
            {
                ConsoleError("The plugin is paused because its configuration could not be loaded. Repair the original file and reload the plugin.");
                return;
            }

            LoadMessages();
            debugEnabled = _config.DebugSettings[ConfigDefaultKeys.DebugEnabled].ToBool();
            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                ConsoleWarn("Verbose debugging overrides voting API responses. Do not enable it on a production server.");
            }

            dataLoaded = LoadVoteData();
            dataLoaded &= LoadPendingRewardsData();
            dataLoaded &= LoadPendingClaimsData();
            if (!dataLoaded)
            {
                ConsoleError("Voting is paused to protect unreadable data. Original files were not overwritten. Repair the files and reload the plugin.");
                return;
            }

            if (!RecoverResetJournal())
            {
                return;
            }

            if (wipeResetRequested)
            {
                wipeResetRequested = false;
                ResetAllVoteData();
            }
        }

        private void OnServerInitialized()
        {
            if (!configurationLoaded || !dataLoaded)
            {
                return;
            }

            ConsoleLog(CanProcessVotes() ? "Easy Vote Extended has been initialized..." : "Easy Vote Extended is waiting for its pending reset to finish safely.");
            StartRequestQueueProcessor();
            StartAutomaticVoteChecks();
            timer.Every(LocalProcessingInterval, ProcessLocalPendingWork);
            timer.Every(1f, ProcessDiscordQueue);
            RecoverPendingClaimTransactions();

            timer.Once(2f, () =>
            {
                if (!CanProcessVotes())
                {
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
        }

        private void Unload()
        {
            unloading = true;
            activeClaimRequests.Clear();
            pendingStatusChecks.Clear();
            statusRequestTokens.Clear();
            scheduledVoteChecks.Clear();
            reportedConfigurationWarnings.Clear();
            claimRequestQueue.Clear();
            voteRequestQueue.Clear();
            commandCooldowns.Clear();
            scheduledClaimRetries.Clear();
            discordQueue.Clear();
            discordRequestToken = null;

            // Preserve a reset intent if storage recovered just before unloading.
            if (dataLoaded && pendingResetJournal != null)
            {
                TryWriteJson(GetDataPath(ResetJournalDataFileName), pendingResetJournal);
            }

            // Do not overwrite unreadable files or a reset that still needs recovery.
            if (dataLoaded && !resetInProgress && claimsDirty)
            {
                SavePendingClaimsData();
            }
        }

        private void OnNewSave(string filename)
        {
            if (!configurationLoaded || !_config.PluginSettings[ConfigDefaultKeys.ClearRewardsOnWipe].ToBool())
            {
                return;
            }

            if (!dataLoaded || resetInProgress)
            {
                wipeResetRequested = true;
                return;
            }

            ConsoleLog("New map data detected. Resetting stored votes and pending rewards.");
            ResetAllVoteData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!CanProcessVotes() || player == null)
            {
                return;
            }

            CheckIfPlayerDataExists(player);
            ulong connectedPlayerId = player.userID;
            timer.Once(2f, () =>
            {
                if (!CanProcessVotes())
                {
                    return;
                }

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
            if (!CanProcessVotes() || player == null)
            {
                return;
            }

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
            PendingClaimTransaction transaction;
            if (!ConsumeClaimRequest(pendingClaimKey, transactionId, requestToken, out transaction))
            {
                return;
            }

            if (debugEnabled)
            {
                _Debug($"Claim response: transaction={transaction.TransactionId}, player={transaction.PlayerId}, site={transaction.Site}, HTTP={code}.");
            }

            if (code != 200)
            {
                FailClaimRequest(pendingClaimKey, transaction, $"Claim HTTP status {code}.");
                return;
            }

            response = response?.Trim();
            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                response = _config.DebugSettings[ConfigDefaultKeys.ClaimAPIRepsonseCode];
            }

            if (response == "1")
            {
                ConfirmClaimTransaction(pendingClaimKey, transaction);
                return;
            }

            if (response == "2" && transaction.HadAmbiguousFailure)
            {
                // This recovery assumes only one consumer claims this external vote.
                ConfirmClaimTransaction(pendingClaimKey, transaction);
                return;
            }

            if (response == "0" && transaction.HadAmbiguousFailure)
            {
                MarkClaimForReview(pendingClaimKey, transaction, "The claim returned 0 after an ambiguous request. Verify the provider before granting or discarding it.");
                return;
            }

            if (response == "0" || response == "2")
            {
                if (!CloseClaimTransaction(pendingClaimKey, transaction))
                {
                    return;
                }

                BasePlayer player = FindPlayer(transaction.PlayerId);
                if (transaction.NotifyPlayer && player != null && player.IsConnected)
                {
                    string messageKey = response == "2" ? "AlreadyVoted" : "ClaimStatus";
                    player.ChatMessage(_lang(messageKey, player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], transaction.Site, transaction.ServerName));
                }

                return;
            }

            FailClaimRequest(pendingClaimKey, transaction, "Unexpected claim response; expected plain 0, 1 or 2.");
        }

        private bool TryGetActiveClaimTransaction(string pendingClaimKey, string transactionId, out PendingClaimTransaction transaction)
        {
            transaction = null;

            return CanProcessVotes() && !string.IsNullOrEmpty(transactionId) &&
                pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) && transaction != null &&
                string.Equals(transaction.TransactionId, transactionId, StringComparison.Ordinal);
        }

        private void ConfirmClaimTransaction(string pendingClaimKey, PendingClaimTransaction transaction)
        {
            PendingClaimTransaction current;
            if (transaction == null || !TryGetActiveClaimTransaction(pendingClaimKey, transaction.TransactionId, out current) || !ReferenceEquals(current, transaction))
            {
                return;
            }

            if (transaction.TargetVoteCount <= 0)
            {
                int highestVoteCount = GetStoredVoteCount(transaction.PlayerId.ToString());
                foreach (PendingClaimTransaction pending in pendingClaimTransactions.Values)
                {
                    if (pending != null && pending.PlayerId == transaction.PlayerId && pending.State == ClaimStateConfirmed)
                    {
                        highestVoteCount = Math.Max(highestVoteCount, pending.TargetVoteCount);
                    }
                }

                if (highestVoteCount == int.MaxValue)
                {
                    MarkClaimForReview(pendingClaimKey, transaction, "The vote counter has reached its maximum value. Correct the counter before resolving this claim.");
                    return;
                }

                transaction.TargetVoteCount = highestVoteCount + 1;
            }

            if (transaction.RewardCommands == null)
            {
                string playerName = GetResolvedPlayerName(FindPlayer(transaction.PlayerId), transaction.PlayerName, transaction.PlayerId.ToString());
                transaction.RewardCommands = BuildRewardCommands(transaction.PlayerId, playerName, transaction.TargetVoteCount, _config.PluginSettings[ConfigDefaultKeys.RewardIsCumulative].ToBool());
            }

            transaction.State = ClaimStateConfirmed;
            transaction.NeedsStatusCheck = false;
            transaction.NextAttemptUtcTicks = 0;
            if (!SavePendingClaimsData())
            {
                ScheduleClaimRetry(pendingClaimKey);
                return;
            }

            if (debugEnabled)
            {
                _Debug($"Claim confirmed: transaction={transaction.TransactionId}, player={transaction.PlayerId}, target votes={transaction.TargetVoteCount}.");
            }

            FinalizeConfirmedClaimTransaction(pendingClaimKey, transaction.TransactionId);
        }

        private void FinalizeConfirmedClaimTransaction(string pendingClaimKey, string transactionId)
        {
            PendingClaimTransaction transaction;
            if (!TryGetActiveClaimTransaction(pendingClaimKey, transactionId, out transaction) || transaction.State != ClaimStateConfirmed)
            {
                return;
            }

            if (transaction.NextAttemptUtcTicks > DateTime.UtcNow.Ticks || !finalizingPlayers.Add(transaction.PlayerId))
            {
                return;
            }

            try
            {
                if (pendingClaimTransactions.Values.Any(other => other != null && !ReferenceEquals(other, transaction) && other.PlayerId == transaction.PlayerId &&
                    other.State == ClaimStateConfirmed && other.TargetVoteCount > 0 && other.TargetVoteCount < transaction.TargetVoteCount))
                {
                    ScheduleClaimRetry(pendingClaimKey);
                    return;
                }

                if (transaction.TargetVoteCount <= 0 || transaction.RewardCommands == null)
                {
                    finalizingPlayers.Remove(transaction.PlayerId);
                    ConfirmClaimTransaction(pendingClaimKey, transaction);
                    return;
                }

                // Persist confirmation before changing the counter or preparing delivery.
                if (claimsDirty && !SavePendingClaimsData())
                {
                    ScheduleClaimRetry(pendingClaimKey);
                    return;
                }

                string steamId = transaction.PlayerId.ToString();
                int currentVoteCount = GetStoredVoteCount(steamId);
                if (currentVoteCount < transaction.TargetVoteCount && !TrySetStoredVoteCount(steamId, transaction.TargetVoteCount))
                {
                    ScheduleClaimRetry(pendingClaimKey);
                    return;
                }

                bool rewardConfigured = transaction.RewardCommands.Any(command => !string.IsNullOrWhiteSpace(command));
                if (rewardConfigured && !QueuePendingRewardTransaction(steamId, transaction.TransactionId, transaction.RewardCommands))
                {
                    ScheduleClaimRetry(pendingClaimKey);
                    return;
                }

                // Delivery remains locked while this confirmed transaction exists.
                if (!RemovePendingClaimTransaction(pendingClaimKey))
                {
                    ScheduleClaimRetry(pendingClaimKey);
                    return;
                }

                BasePlayer player = FindPlayer(transaction.PlayerId);
                string playerName = GetResolvedPlayerName(player, transaction.PlayerName, steamId);
                int generation = runtimeGeneration;
                RewardDeliveryResult deliveryResult = rewardConfigured ? DeliverPendingRewards(player, false) : RewardDeliveryResult.None;
                if (!CanProcessVotes() || generation != runtimeGeneration)
                {
                    return;
                }

                bool dispatched = deliveryResult == RewardDeliveryResult.Dispatched;
                bool dispatchFailed = deliveryResult == RewardDeliveryResult.Failed;
                string displayedVoteCount = GetStoredVoteCount(steamId).ToString();
                if (player != null && player.IsConnected)
                {
                    string messageKey = !rewardConfigured ? "ThankYouNoReward" : (dispatchFailed ? "ThankYouDispatchFailed" : (dispatched ? "ThankYou" : "ThankYouPending"));
                    player.ChatMessage(_lang(messageKey, player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], displayedVoteCount, transaction.Site));
                }

                if (_config.Discord[ConfigDefaultKeys.DiscordEnabled].ToBool())
                {
                    string messageKey = !rewardConfigured ? "DiscordWebhookMessageNoReward" : (dispatchFailed ? "DiscordWebhookMessageFailed" : (dispatched ? "DiscordWebhookMessage" : "DiscordWebhookMessagePending"));
                    DiscordSendMessage(_lang(messageKey, null, playerName, transaction.ServerName, transaction.Site));
                }

                if (_config.NotificationSettings[ConfigDefaultKeys.GlobalChatAnnouncements].ToBool())
                {
                    string messageKey = !rewardConfigured ? "GlobalChatAnnouncementsNoReward" : (dispatchFailed ? "GlobalChatAnnouncementsFailed" : (dispatched ? "GlobalChatAnnouncements" : "GlobalChatAnnouncementsPending"));
                    foreach (BasePlayer recipient in BasePlayer.activePlayerList.ToArray())
                    {
                        if (recipient != null && recipient.IsConnected)
                        {
                            recipient.ChatMessage(_lang(messageKey, recipient.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], playerName, displayedVoteCount));
                        }
                    }
                }
            }
            finally
            {
                finalizingPlayers.Remove(transaction.PlayerId);
            }
        }

        private void HandleStatusWebRequestCallback(int code, string response, ulong playerId, string playerName, string serverName, string site, bool notifyPlayer, string pendingStatusKey, string statusRequestToken)
        {
            if (!TryConsumeStatusRequest(pendingStatusKey, statusRequestToken))
            {
                return;
            }

            if (debugEnabled)
            {
                _Debug($"Vote status response: player={playerId}, server={serverName}, site={site}, HTTP={code}.");
            }

            if (code != 200)
            {
                ConsoleWarn($"Vote status request failed for {playerId} on {site} ({serverName}): HTTP {code}. Check the provider, connectivity and configured credentials.");
                return;
            }

            response = response?.Trim();
            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                response = _config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode];
            }

            string pendingClaimKey = GetPendingClaimKey(playerId, serverName, site);
            if (pendingClaimTransactions.ContainsKey(pendingClaimKey))
            {
                return;
            }

            if (response == "1")
            {
                // Keep the exact username used by this status request for its claim.
                EnqueueClaimRequest(playerId, playerName, serverName, site, notifyPlayer);
                return;
            }

            BasePlayer player = FindPlayer(playerId);
            if (response == "0" || response == "2")
            {
                if (notifyPlayer && player != null && player.IsConnected)
                {
                    if (response == "0")
                    {
                        player.ChatMessage(_lang("NoRewards", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], serverName, site));
                    }
                    else
                    {
                        player.ChatMessage(_lang("AlreadyVoted", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], site));
                    }
                }

                return;
            }

            ConsoleWarn($"Unexpected status response from {site} for {playerId} on {serverName}. Expected plain 0, 1 or 2.");
        }

        private bool IsActiveStatusRequest(string pendingStatusKey, string statusRequestToken)
        {
            string activeToken;
            return CanProcessVotes() && !string.IsNullOrEmpty(statusRequestToken) && statusRequestTokens.TryGetValue(pendingStatusKey, out activeToken) && string.Equals(activeToken, statusRequestToken, StringComparison.Ordinal);
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

            foreach (string pendingStatusKey in statusRequestTokens.Keys.Where(key => key.StartsWith(steamId + ":", StringComparison.Ordinal)).ToList())
            {
                statusRequestTokens.Remove(pendingStatusKey);
                pendingStatusChecks.Remove(pendingStatusKey);
            }
        }

        private void EnqueueClaimRequest(ulong playerId, string playerName, string serverName, string site, bool notifyPlayer)
        {
            if (!CanProcessVotes())
            {
                return;
            }

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
                    State = ClaimStateCreated,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks
                };

                pendingClaimTransactions[pendingClaimKey] = transaction;
            }
            else
            {
                transaction.NotifyPlayer |= notifyPlayer;
            }

            if (!SavePendingClaimsData())
            {
                ScheduleClaimRetry(pendingClaimKey);
                return;
            }

            QueuePendingClaimTransaction(pendingClaimKey);
        }

        private void QueuePendingClaimTransaction(string pendingClaimKey)
        {
            PendingClaimTransaction transaction;
            if (!CanProcessVotes() || !pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) || transaction == null)
            {
                return;
            }

            if (activeClaimRequests.ContainsKey(pendingClaimKey))
            {
                return;
            }

            if (transaction.NextAttemptUtcTicks > DateTime.UtcNow.Ticks)
            {
                ScheduleClaimRetry(pendingClaimKey);
                return;
            }

            if (transaction.State == ClaimStateConfirmed)
            {
                FinalizeConfirmedClaimTransaction(pendingClaimKey, transaction.TransactionId);
                return;
            }

            if (transaction.State == ClaimStateClosed)
            {
                if (!SavePendingClaimsData() || !RemovePendingClaimTransaction(pendingClaimKey))
                {
                    ScheduleClaimRetry(pendingClaimKey);
                }

                return;
            }

            if (transaction.State == ClaimStateManualReview)
            {
                return;
            }

            bool reconcile = transaction.NeedsStatusCheck || transaction.State == ClaimStateUncertain;
            int attempts = reconcile ? transaction.RecoveryAttempts : transaction.Attempts;
            if (attempts >= MaximumClaimAttempts)
            {
                MarkClaimForReview(pendingClaimKey, transaction, "The retry limit was reached. The transaction has been preserved for manual review.");
                return;
            }

            string requestUrl;
            if (!TryBuildTransactionUrl(transaction, reconcile ? ConfigDefaultKeys.apiStatus : ConfigDefaultKeys.apiClaim, out requestUrl))
            {
                MarkClaimForReview(pendingClaimKey, transaction, "The configured provider or credentials could not be used to build the request URL.");
                return;
            }

            string transactionId = transaction.TransactionId;
            string requestToken = Guid.NewGuid().ToString("N");
            activeClaimRequests[pendingClaimKey] = requestToken;
            scheduledClaimRetries.Remove(pendingClaimKey);

            QueueVoteRequest(() =>
            {
                PendingClaimTransaction current;
                string activeToken;
                if (!TryGetActiveClaimTransaction(pendingClaimKey, transactionId, out current) || !activeClaimRequests.TryGetValue(pendingClaimKey, out activeToken) || activeToken != requestToken)
                {
                    ReleaseClaimRequest(pendingClaimKey, requestToken);
                    return;
                }

                string previousState = current.State;
                if (reconcile)
                {
                    current.RecoveryAttempts++;
                }
                else
                {
                    current.Attempts++;
                }

                current.State = ClaimStateSent;
                current.NextAttemptUtcTicks = 0;
                if (!SavePendingClaimsData())
                {
                    if (reconcile)
                    {
                        current.RecoveryAttempts--;
                    }
                    else
                    {
                        current.Attempts--;
                    }

                    current.State = previousState;
                    ReleaseClaimRequest(pendingClaimKey, requestToken);
                    ScheduleClaimRetry(pendingClaimKey);
                    return;
                }

                if (debugEnabled)
                {
                    _Debug($"Sending {(reconcile ? "recovery status" : "claim")} request: transaction={transactionId}, player={current.PlayerId}, site={current.Site}, attempt={(reconcile ? current.RecoveryAttempts : current.Attempts)}.");
                }

                Action<int, string> callback = (code, response) =>
                {
                    if (reconcile)
                    {
                        HandleClaimReconciliation(code, response, pendingClaimKey, transactionId, requestToken);
                    }
                    else
                    {
                        HandleClaimWebRequestCallback(code, response, pendingClaimKey, transactionId, requestToken);
                    }
                };

                try
                {
                    webrequest.Enqueue(requestUrl, null, (code, response) => NextTick(() => callback(code, response)), this, RequestMethod.GET, null, VoteApiRequestTimeout);
                    timer.Once(VoteApiRequestTimeout + 5f, () => callback(-1, null));
                }
                catch (Exception)
                {
                    callback(-1, null);
                }
            }, true);
        }

        private void ScheduleClaimRetry(string pendingClaimKey)
        {
            PendingClaimTransaction transaction;
            if (!CanProcessVotes() || !pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction) || transaction == null || transaction.State == ClaimStateManualReview)
            {
                return;
            }

            string token;
            if (scheduledClaimRetries.TryGetValue(pendingClaimKey, out token))
            {
                return;
            }

            bool localCompletion = transaction.State == ClaimStateConfirmed || transaction.State == ClaimStateClosed;
            int attempts = transaction.NeedsStatusCheck ? transaction.RecoveryAttempts : transaction.Attempts;
            if (!localCompletion && attempts >= MaximumClaimAttempts && !activeClaimRequests.ContainsKey(pendingClaimKey))
            {
                MarkClaimForReview(pendingClaimKey, transaction, "The retry limit was reached. Inspect the preserved transaction with eve.pending.");
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            if (transaction.NextAttemptUtcTicks <= now)
            {
                int backoffAttempts = Math.Max(transaction.Attempts, transaction.RecoveryAttempts);
                double delay = localCompletion ? 10d : (backoffAttempts <= 3 ? Math.Max(10d, backoffAttempts * 10d) : 300d);
                transaction.NextAttemptUtcTicks = now + (long)(delay * TimeSpan.TicksPerSecond);
                SavePendingClaimsData();
            }

            float remaining = (float)Math.Max(0.1d, Math.Min(300d, (transaction.NextAttemptUtcTicks - now) / (double)TimeSpan.TicksPerSecond));
            string transactionId = transaction.TransactionId;
            token = Guid.NewGuid().ToString("N");
            string retryToken = token;
            scheduledClaimRetries[pendingClaimKey] = retryToken;
            if (debugEnabled)
            {
                _Debug($"Retry scheduled: transaction={transactionId}, state={transaction.State}, delay={remaining:0.0}s.");
            }

            timer.Once(remaining, () =>
            {
                string currentToken;
                PendingClaimTransaction current;
                if (!scheduledClaimRetries.TryGetValue(pendingClaimKey, out currentToken) || currentToken != retryToken || !TryGetActiveClaimTransaction(pendingClaimKey, transactionId, out current))
                {
                    return;
                }

                scheduledClaimRetries.Remove(pendingClaimKey);
                QueuePendingClaimTransaction(pendingClaimKey);
            });
        }

        private void RecoverPendingClaimTransactions()
        {
            if (!CanProcessVotes())
            {
                return;
            }

            bool changed = false;
            foreach (PendingClaimTransaction transaction in pendingClaimTransactions.Values)
            {
                if (string.IsNullOrEmpty(transaction.TransactionId))
                {
                    transaction.TransactionId = Guid.NewGuid().ToString("N");
                    changed = true;
                }

                if (transaction.CreatedUtcTicks == 0)
                {
                    transaction.CreatedUtcTicks = DateTime.UtcNow.Ticks;
                    changed = true;
                }

                if (string.IsNullOrEmpty(transaction.State) || transaction.State == ClaimStateSent)
                {
                    transaction.State = transaction.Attempts > 0 ? ClaimStateUncertain : ClaimStateCreated;
                    transaction.HadAmbiguousFailure |= transaction.Attempts > 0;
                    changed = true;
                }

                if (transaction.State == ClaimStateUncertain)
                {
                    transaction.NeedsStatusCheck = true;
                    transaction.HadAmbiguousFailure = true;
                    changed = true;
                }
            }

            if (changed && !SavePendingClaimsData())
            {
                foreach (string key in pendingClaimTransactions.Keys.ToArray())
                {
                    ScheduleClaimRetry(key);
                }

                return;
            }

            foreach (KeyValuePair<string, PendingClaimTransaction> entry in pendingClaimTransactions.OrderBy(entry => entry.Value.TargetVoteCount).ToArray())
            {
                QueuePendingClaimTransaction(entry.Key);
            }
        }

        private string GetPendingClaimKey(ulong playerId, string serverName, string site)
        {
            return $"{playerId}:{serverName}:{site}";
        }

        private bool RemovePendingClaimTransaction(string pendingClaimKey)
        {
            PendingClaimTransaction transaction;
            if (!pendingClaimTransactions.TryGetValue(pendingClaimKey, out transaction))
            {
                return true;
            }

            pendingClaimTransactions.Remove(pendingClaimKey);
            if (!SavePendingClaimsData())
            {
                // Keep delivery locked until the removal is durable.
                pendingClaimTransactions[pendingClaimKey] = transaction;
                return false;
            }

            scheduledClaimRetries.Remove(pendingClaimKey);
            reportedConfigurationWarnings.Remove($"review:{transaction.TransactionId}");
            string requestToken;
            if (activeClaimRequests.TryGetValue(pendingClaimKey, out requestToken))
            {
                ReleaseClaimRequest(pendingClaimKey, requestToken);
            }

            return true;
        }

        private bool ConsumeClaimRequest(string pendingClaimKey, string transactionId, string requestToken, out PendingClaimTransaction transaction)
        {
            transaction = null;
            string activeToken;
            if (!TryGetActiveClaimTransaction(pendingClaimKey, transactionId, out transaction) || !activeClaimRequests.TryGetValue(pendingClaimKey, out activeToken) || activeToken != requestToken)
            {
                return false;
            }

            ReleaseClaimRequest(pendingClaimKey, requestToken);
            return true;
        }

        private void ReleaseClaimRequest(string pendingClaimKey, string requestToken)
        {
            string activeToken;
            if (activeClaimRequests.TryGetValue(pendingClaimKey, out activeToken) && activeToken == requestToken)
            {
                activeClaimRequests.Remove(pendingClaimKey);
            }
        }

        private void FailClaimRequest(string key, PendingClaimTransaction transaction, string reason)
        {
            transaction.State = ClaimStateUncertain;
            transaction.HadAmbiguousFailure = true;
            transaction.NeedsStatusCheck = true;
            transaction.ReviewReason = reason;
            SavePendingClaimsData();
            ConsoleWarn($"Vote transaction {transaction.TransactionId} for {transaction.PlayerId} on {transaction.Site}: {reason}");
            ScheduleClaimRetry(key);
        }

        private void MarkClaimForReview(string key, PendingClaimTransaction transaction, string reason)
        {
            transaction.State = ClaimStateManualReview;
            transaction.ReviewReason = reason;
            transaction.NextAttemptUtcTicks = 0;
            scheduledClaimRetries.Remove(key);
            SavePendingClaimsData();
            ConsoleWarn($"Vote transaction {transaction.TransactionId} for {transaction.PlayerId} requires manual review: {reason} Use eve.pending and eve.resolve.");
        }

        private bool CloseClaimTransaction(string key, PendingClaimTransaction transaction)
        {
            transaction.State = ClaimStateClosed;
            transaction.NextAttemptUtcTicks = 0;
            if (!SavePendingClaimsData() || !RemovePendingClaimTransaction(key))
            {
                ScheduleClaimRetry(key);
                return false;
            }

            return true;
        }

        private void HandleClaimReconciliation(int code, string response, string key, string transactionId, string requestToken)
        {
            PendingClaimTransaction transaction;
            if (!ConsumeClaimRequest(key, transactionId, requestToken, out transaction))
            {
                return;
            }

            if (debugEnabled)
            {
                _Debug($"Recovery response: transaction={transaction.TransactionId}, player={transaction.PlayerId}, site={transaction.Site}, HTTP={code}.");
            }

            if (code != 200)
            {
                FailClaimRequest(key, transaction, $"Recovery status HTTP {code}.");
                return;
            }

            response = response?.Trim();
            if (_config.DebugSettings[ConfigDefaultKeys.VerboseDebugEnabled].ToBool())
            {
                response = _config.DebugSettings[ConfigDefaultKeys.CheckAPIResponseCode];
            }

            if (response == "1")
            {
                transaction.State = ClaimStateCreated;
                transaction.RecoveryAttempts = 0;
                transaction.NeedsStatusCheck = false;
                transaction.NextAttemptUtcTicks = 0;
                if (!SavePendingClaimsData())
                {
                    ScheduleClaimRetry(key);
                    return;
                }

                QueuePendingClaimTransaction(key);
            }
            else if (response == "2" && transaction.HadAmbiguousFailure)
            {
                ConfirmClaimTransaction(key, transaction);
            }
            else if (response == "0" || response == "2")
            {
                MarkClaimForReview(key, transaction, "The provider no longer reports an unclaimed vote after an ambiguous request. Verify the outcome before resolving it.");
            }
            else
            {
                FailClaimRequest(key, transaction, "Unexpected recovery status response; expected plain 0, 1 or 2.");
            }
        }

        private bool TryBuildTransactionUrl(PendingClaimTransaction transaction, string apiKey, out string url)
        {
            url = null;
            if (transaction == null || _config.Servers == null)
            {
                return false;
            }

            KeyValuePair<string, Dictionary<string, string>> server = _config.Servers.FirstOrDefault(entry => entry.Key.Equals(transaction.ServerName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(server.Key) || server.Value == null)
            {
                return false;
            }

            KeyValuePair<string, string> site = server.Value.FirstOrDefault(entry => entry.Key.Equals(transaction.Site, StringComparison.OrdinalIgnoreCase));
            string configuredSiteName;
            Dictionary<string, string> apiConfiguration;
            string serverId;
            string serverKey;
            if (string.IsNullOrEmpty(site.Key) || !TryGetVoteSiteConfiguration(site.Key, out configuredSiteName, out apiConfiguration) || !TryParseServerCredentials(site.Value, out serverId, out serverKey))
            {
                return false;
            }

            try
            {
                url = FormatApiUrl(apiConfiguration, apiKey, transaction.PlayerId, transaction.PlayerName, serverId, serverKey);
                Uri parsed;
                return Uri.TryCreate(url, UriKind.Absolute, out parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasUncommittedRewards(ulong playerId)
        {
            return pendingClaimTransactions.Values.Any(transaction => transaction != null && transaction.PlayerId == playerId && transaction.State == ClaimStateConfirmed);
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
            string encodedPlayerIdentifier = usernameApiEnabled ? Uri.EscapeDataString(playerName ?? string.Empty) : playerId.ToString();

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

            if (apiConfiguration == null || !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiClaim) || !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiStatus) ||
                !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiLink) || !apiConfiguration.ContainsKey(ConfigDefaultKeys.apiUsername) || string.IsNullOrWhiteSpace(apiConfiguration[ConfigDefaultKeys.apiClaim]) ||
                string.IsNullOrWhiteSpace(apiConfiguration[ConfigDefaultKeys.apiStatus]) || string.IsNullOrWhiteSpace(apiConfiguration[ConfigDefaultKeys.apiLink]))
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

            string[] idKey = configuredValue.Split(new[]
            {
                ':'
            }, 2);
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

            if (serverId.Equals("ID", StringComparison.OrdinalIgnoreCase) || serverKey.Equals("KEY", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private int GetStoredVoteCount(string steamId)
        {
            if (!dataLoaded || DataFile == null || string.IsNullOrWhiteSpace(steamId))
            {
                return 0;
            }

            object value = DataFile[steamId];
            int count;
            return value != null && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ? Math.Max(0, count) : 0;
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
            if (destination == null || !_config.Rewards.TryGetValue(rewardKey, out configuredCommands) || configuredCommands == null)
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
            if (!CanProcessVotes() || string.IsNullOrWhiteSpace(steamId) || string.IsNullOrWhiteSpace(transactionId) || commands == null || commands.Count == 0)
            {
                return false;
            }

            List<string> pendingCommands;
            bool existed = pendingRewardCommands.TryGetValue(steamId, out pendingCommands) && pendingCommands != null;
            if (!existed)
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
                if (!existed)
                {
                    pendingRewardCommands.Remove(steamId);
                }

                return false;
            }

            if (debugEnabled)
            {
                _Debug($"Reward queue saved: player={steamId}, transaction={transactionId}, commands={commands.Count}.");
            }

            return true;
        }

        private string GetPendingRewardTransactionMarker(string transactionId)
        {
            return PendingRewardTransactionPrefix + transactionId;
        }

        private bool IsPendingRewardTransactionMarker(string value)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(PendingRewardTransactionPrefix, StringComparison.Ordinal);
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

        private RewardDeliveryResult DeliverPendingRewards(BasePlayer player, bool notifyPlayer = true)
        {
            if (!CanProcessVotes() || player == null || !player.IsConnected || player.IsSleeping())
            {
                return RewardDeliveryResult.Pending;
            }

            ulong deliveryPlayerId = player.userID;
            string steamId = player.UserIDString;
            List<string> commands;
            if (!pendingRewardCommands.TryGetValue(steamId, out commands) || commands == null || commands.Count == 0)
            {
                return RewardDeliveryResult.None;
            }

            if (HasUncommittedRewards(deliveryPlayerId) || !deliveringPlayers.Add(deliveryPlayerId))
            {
                return RewardDeliveryResult.Pending;
            }

            int dispatched = 0;
            int processed = 0;
            bool executionFailed = false;
            int generation = runtimeGeneration;

            try
            {
                while (pendingRewardCommands.TryGetValue(steamId, out commands) && commands != null && commands.Count > 0 && processed < MaximumCommandsPerDelivery)
                {
                    if (!CanProcessVotes() || generation != runtimeGeneration || player == null || !player.IsConnected || player.IsSleeping() || HasUncommittedRewards(deliveryPlayerId))
                    {
                        return dispatched > 0 ? RewardDeliveryResult.Partial : RewardDeliveryResult.Pending;
                    }

                    string command = commands[0];
                    commands.RemoveAt(0);
                    bool removedPlayerEntry = commands.Count == 0;
                    if (removedPlayerEntry)
                    {
                        pendingRewardCommands.Remove(steamId);
                    }

                    // Save removal before dispatch; never automatically replay uncertain commands.
                    if (!SavePendingRewardsData())
                    {
                        if (removedPlayerEntry)
                        {
                            pendingRewardCommands[steamId] = commands;
                        }

                        commands.Insert(0, command);
                        return dispatched > 0 ? RewardDeliveryResult.Partial : RewardDeliveryResult.Pending;
                    }

                    processed++;
                    if (IsPendingRewardTransactionMarker(command) || string.IsNullOrWhiteSpace(command))
                    {
                        continue;
                    }

                    if (debugEnabled)
                    {
                        _Debug($"Dispatching a pending vote reward command for {steamId}.");
                    }

                    try
                    {
                        rust.RunServerCommand(command);
                        dispatched++;
                    }
                    catch (Exception)
                    {
                        executionFailed = true;
                        ConsoleError($"A vote reward command threw an exception for {steamId}. It will not be replayed automatically; check the server/plugin command logs.");
                        break;
                    }
                }

                if (!CanProcessVotes() || generation != runtimeGeneration)
                {
                    return dispatched > 0 ? RewardDeliveryResult.Partial : RewardDeliveryResult.Pending;
                }

                bool remaining = pendingRewardCommands.TryGetValue(steamId, out commands) && commands != null && commands.Count > 0;
                if (executionFailed)
                {
                    return RewardDeliveryResult.Failed;
                }

                if (remaining)
                {
                    return dispatched > 0 ? RewardDeliveryResult.Partial : RewardDeliveryResult.Pending;
                }

                if (dispatched > 0 && notifyPlayer && player != null && player.IsConnected && CanProcessVotes() && generation == runtimeGeneration)
                {
                    player.ChatMessage(_lang("PendingRewardsDelivered", steamId, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
                }

                return dispatched > 0 ? RewardDeliveryResult.Dispatched : RewardDeliveryResult.None;
            }
            finally
            {
                deliveringPlayers.Remove(deliveryPlayerId);
            }
        }

        private string ParseRewardCommand(string steamId, string playerName, string command)
        {
            string safePlayerName = string.IsNullOrWhiteSpace(playerName) ? steamId : playerName.Replace("\r", " ").Replace("\n", " ").Replace(";", string.Empty).Replace("\"", "'");

            return command.Replace("{playerid}", steamId).Replace("{playername}", safePlayerName);
        }

        private void CheckIfPlayerDataExists(BasePlayer player)
        {
            if (!CanProcessVotes() || player == null || !IsValidSteamId(player.userID))
            {
                return;
            }

            if (DataFile[player.UserIDString] == null)
            {
                TrySetStoredVoteCount(player.UserIDString, 0);
            }
        }

        private bool ResetAllVoteData()
        {
            return BeginVoteReset(null);
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
            if (!CanProcessVotes() || player == null || !player.IsConnected || !IsValidSteamId(player.userID))
            {
                return;
            }

            bool waitMessageSent = false;
            foreach (KeyValuePair<string, Dictionary<string, string>> server in _config.Servers)
            {
                if (server.Value == null)
                {
                    continue;
                }

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
                        continue;
                    }

                    string pendingStatusKey = GetPendingClaimKey(player.userID, server.Key, configuredSiteName);
                    PendingClaimTransaction existing;
                    if (pendingClaimTransactions.TryGetValue(pendingStatusKey, out existing))
                    {
                        if (existing.State == ClaimStateManualReview)
                        {
                            WarnConfigurationOnce($"review:{existing.TransactionId}", $"Vote transaction {existing.TransactionId} for {existing.PlayerId} on {existing.Site} needs review. Use eve.pending and eve.resolve; it was not discarded.");
                            if (notifyPlayer)
                            {
                                player.ChatMessage(_lang("ClaimNeedsReview", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], configuredSiteName));
                            }
                        }
                        else
                        {
                            QueuePendingClaimTransaction(pendingStatusKey);
                        }

                        continue;
                    }

                    string statusUrl;

                    try
                    {
                        statusUrl = FormatApiUrl(apiConfiguration, ConfigDefaultKeys.apiStatus, player, serverId, serverKey);
                        Uri uri;
                        if (!Uri.TryCreate(statusUrl, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            throw new FormatException();
                        }
                    }
                    catch (Exception)
                    {
                        WarnConfigurationOnce($"url:{server.Key}:{configuredSiteName}", $"The status URL template for {configuredSiteName} on {server.Key} is invalid.");
                        continue;
                    }

                    if (!pendingStatusChecks.Add(pendingStatusKey))
                    {
                        continue;
                    }

                    ulong requestPlayerId = player.userID;
                    string requestPlayerName = player.displayName;
                    string requestServerName = server.Key;
                    string requestSiteName = configuredSiteName;
                    string statusRequestToken = Guid.NewGuid().ToString("N");
                    statusRequestTokens[pendingStatusKey] = statusRequestToken;
                    if (!waitMessageSent && notifyPlayer && _config.NotificationSettings[ConfigDefaultKeys.PleaseWaitMessage].ToBool())
                    {
                        player.ChatMessage(_lang("PleaseWait", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
                        waitMessageSent = true;
                    }

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

                        Action<int, string> callback = (code, response) => HandleStatusWebRequestCallback(code, response, requestPlayerId, requestPlayerName, requestServerName, requestSiteName, notifyPlayer, pendingStatusKey, statusRequestToken);

                        try
                        {
                            webrequest.Enqueue(statusUrl, null, (code, response) => NextTick(() => callback(code, response)), this, RequestMethod.GET, null, VoteApiRequestTimeout);
                            timer.Once(VoteApiRequestTimeout + 5f, () => callback(-1, null));
                        }
                        catch (Exception)
                        {
                            callback(-1, null);
                        }
                    });
                }
            }
        }

        private void StartRequestQueueProcessor()
        {
            timer.Every(VoteRequestSpacing, () =>
            {
                if (!CanProcessVotes() || (claimRequestQueue.Count == 0 && voteRequestQueue.Count == 0))
                {
                    return;
                }

                bool takeClaim = claimRequestQueue.Count > 0 && (consecutiveClaimRequests < 4 || voteRequestQueue.Count == 0);
                Action queuedRequest = takeClaim ? claimRequestQueue.Dequeue() : voteRequestQueue.Dequeue();
                consecutiveClaimRequests = takeClaim ? Math.Min(4, consecutiveClaimRequests + 1) : 0;

                try
                {
                    queuedRequest?.Invoke();
                }
                catch (Exception exception)
                {
                    ConsoleError($"Vote request queue failure: {exception.GetType().Name}.");
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

        private bool CanProcessVotes()
        {
            return configurationLoaded && dataLoaded && !unloading && !resetInProgress && !wipeResetRequested;
        }

        private void ProcessLocalPendingWork()
        {
            if (!configurationLoaded || !dataLoaded || unloading)
            {
                return;
            }

            if (resetInProgress)
            {
                if (pendingResetJournal != null && TryWriteJson(GetDataPath(ResetJournalDataFileName), pendingResetJournal))
                {
                    ApplyVoteReset(pendingResetJournal);
                }

                return;
            }

            if (wipeResetRequested)
            {
                wipeResetRequested = false;
                ResetAllVoteData();
                return;
            }

            if (claimsDirty)
            {
                SavePendingClaimsData();
            }

            foreach (KeyValuePair<string, PendingClaimTransaction> entry in pendingClaimTransactions.OrderBy(entry => entry.Value.TargetVoteCount).ToArray())
            {
                PendingClaimTransaction transaction = entry.Value;
                if (transaction.State != ClaimStateManualReview && !scheduledClaimRetries.ContainsKey(entry.Key))
                {
                    QueuePendingClaimTransaction(entry.Key);
                }
            }

            foreach (string steamId in pendingRewardCommands.Keys.ToArray())
            {
                ulong playerId;
                if (ulong.TryParse(steamId, out playerId))
                {
                    BasePlayer player = BasePlayer.FindByID(playerId);
                    if (player != null && player.IsConnected && !player.IsSleeping())
                    {
                        DeliverPendingRewards(player);
                    }
                }
            }
        }

        private void ProcessDiscordQueue()
        {
            if (!CanProcessVotes() || discordRequestToken != null || discordQueue.Count == 0 || discordNextAttemptUtcTicks > DateTime.UtcNow.Ticks)
            {
                return;
            }

            DiscordNotification notification = discordQueue.Peek();
            string token = Guid.NewGuid().ToString("N");
            discordRequestToken = token;
            notification.Attempts++;
            Action<int, string> callback = (code, response) =>
            {
                if (unloading || discordRequestToken != token || discordQueue.Count == 0 || !ReferenceEquals(discordQueue.Peek(), notification))
                {
                    return;
                }

                discordRequestToken = null;
                if (code >= 200 && code < 300)
                {
                    discordQueue.Dequeue();
                    discordNextAttemptUtcTicks = DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond;
                    return;
                }

                if (notification.Attempts >= 4 || (code >= 400 && code < 500 && code != 429))
                {
                    discordQueue.Dequeue();
                    ConsoleWarn($"Discord announcement was not delivered (HTTP {code}). Rewards were not retried.");
                    return;
                }

                double delay = Math.Pow(2d, notification.Attempts);
                if (code == 429 && !string.IsNullOrWhiteSpace(response))
                {
                    try
                    {
                        JToken retryAfter = JObject.Parse(response)["retry_after"];
                        double seconds;
                        if (retryAfter != null && double.TryParse(retryAfter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && !double.IsNaN(seconds) && !double.IsInfinity(seconds))
                        {
                            delay = Math.Max(1d, Math.Min(600d, seconds));
                        }
                    }
                    catch (Exception)
                    {
                        // Use bounded exponential backoff if Discord's response is invalid.
                    }
                }

                discordNextAttemptUtcTicks = DateTime.UtcNow.Ticks + (long)(delay * TimeSpan.TicksPerSecond);
            };

            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            };

            try
            {
                webrequest.Enqueue(notification.Url, notification.Body, (code, response) => NextTick(() => callback(code, response)), this, RequestMethod.POST, headers, VoteApiRequestTimeout);
                timer.Once(VoteApiRequestTimeout + 5f, () => callback(-1, null));
            }
            catch (Exception)
            {
                callback(-1, null);
            }
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
            if (!CanProcessVotes() || delay <= 0 || player == null || scheduledVoteChecks.ContainsKey(player.userID))
            {
                return;
            }

            ulong playerId = player.userID;
            string token = Guid.NewGuid().ToString("N");
            scheduledVoteChecks[playerId] = token;
            Action followUp = null;
            followUp = () =>
            {
                string activeToken;
                if (unloading || !scheduledVoteChecks.TryGetValue(playerId, out activeToken) || activeToken != token)
                {
                    return;
                }

                BasePlayer currentPlayer = BasePlayer.FindByID(playerId);
                if (currentPlayer == null || !currentPlayer.IsConnected)
                {
                    scheduledVoteChecks.Remove(playerId);
                    return;
                }

                if (!CanProcessVotes())
                {
                    // Keep the check scheduled while a storage reset is being retried.
                    timer.Once(LocalProcessingInterval, followUp);
                    return;
                }

                scheduledVoteChecks.Remove(playerId);
                CheckVotingStatus(currentPlayer, false);
            };

            timer.Once(delay, followUp);
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
                if (!CanProcessVotes() || voteRequestQueue.Count > 0)
                {
                    return;
                }

                foreach (BasePlayer player in BasePlayer.activePlayerList.ToArray())
                {
                    if (player == null || !player.IsConnected)
                    {
                        continue;
                    }

                    CheckIfPlayerDataExists(player);
                    CheckVotingStatus(player, false);
                }
            });

            ConsoleLog($"Automatic vote checks for online players will run every {interval} seconds.");
        }

        private int GetAutomaticVoteCheckInterval()
        {
            string configuredInterval;
            int interval;

            if (_config.NotificationSettings.TryGetValue(ConfigDefaultKeys.AutomaticVoteCheckInterval, out configuredInterval) && int.TryParse(configuredInterval, out interval))
            {
                return Math.Max(0, interval);
            }

            return DefaultAutomaticVoteCheckInterval;
        }

        private int GetVoteFollowUpCheckDelay()
        {
            string configuredDelay;
            int delay;

            if (_config.NotificationSettings.TryGetValue(ConfigDefaultKeys.VoteFollowUpCheckDelay, out configuredDelay) && int.TryParse(configuredDelay, out delay))
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

            return _config != null && _config.PluginSettings != null && _config.PluginSettings.TryGetValue(ConfigDefaultKeys.LogEnabled, out loggingSetting) && bool.TryParse(loggingSetting, out loggingEnabled) && loggingEnabled;
        }

        protected void ConsoleError(string message)
        {
            if (IsLoggingEnabled())
            {
                try
                {
                    LogToFile("EasyVoteExtended", $"ERROR: {message}", this);
                }
                catch (Exception)
                {
                    // A logging failure must not interrupt storage error handling.
                }
            }

            Debug.LogError($"ERROR: {message}");
        }

        protected void ConsoleWarn(string message)
        {
            if (IsLoggingEnabled())
            {
                try
                {
                    LogToFile("EasyVoteExtended", $"WARNING: {message}", this);
                }
                catch (Exception)
                {
                    // Keep the console warning available when file logging fails.
                }
            }

            Debug.LogWarning($"WARNING: {message}");
        }

        protected void _Debug(string message)
        {
            if (!debugEnabled || unloading)
            {
                return;
            }

            if (IsLoggingEnabled())
            {
                try
                {
                    LogToFile("EasyVoteExtended", $"DEBUG: {message}", this);
                }
                catch (Exception)
                {
                    // Keep vote processing independent of file logging.
                }
            }

            Puts($"DEBUG: {message}");
        }

        private void DiscordSendMessage(string msg)
        {
            if (!CanProcessVotes())
            {
                return;
            }

            string webhookUrl = _config.Discord[ConfigDefaultKeys.discordWebhookURL];
            if (string.IsNullOrWhiteSpace(webhookUrl) || webhookUrl == "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks")
            {
                return;
            }

            Uri parsed;
            if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                WarnConfigurationOnce("discord-url", "The Discord webhook URL is invalid.");
                return;
            }

            if (discordQueue.Count >= 512)
            {
                WarnConfigurationOnce("discord-queue", "The Discord announcement queue is full. New announcements are being dropped; vote rewards are not affected.");
                return;
            }

            string title;
            string content = msg ?? string.Empty;
            if (_config.Discord.TryGetValue(ConfigDefaultKeys.discordTitle, out title) && !string.IsNullOrWhiteSpace(title))
            {
                content = $"**{title}**\n{content}";
            }

            if (content.Length > 2000)
            {
                int length = char.IsHighSurrogate(content[1999]) ? 1999 : 2000;
                content = content.Substring(0, length);
            }

            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["content"] = content,
                ["allowed_mentions"] = new Dictionary<string, object>
                {
                    ["parse"] = new string[0]
                }
            };

            discordQueue.Enqueue(new DiscordNotification
            {
                Url = webhookUrl,
                Body = JsonConvert.SerializeObject(body)
            });
        }

        ////////////////////////////////////////////////////////////
        // Commands
        ////////////////////////////////////////////////////////////

        [ChatCommand("rewardlist")]
        private void RewardListChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !configurationLoaded)
            {
                return;
            }

            player.ChatMessage(_lang("RewardsListHeader", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));

            List<KeyValuePair<string, List<string>>> configuredRewards = _config.Rewards
                .Where(reward => HasRewardCommands(reward.Value) && (reward.Key == "@" || reward.Key == "first" || IsPositiveNumericRewardKey(reward.Key)))
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
            if (player == null || !configurationLoaded)
            {
                return;
            }

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

                    if (!TryGetVoteSiteConfiguration(configuredVoteSite.Key, out configuredSiteName, out apiConfiguration) || !TryParseServerCredentials(configuredVoteSite.Value, out serverId, out serverKey))
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

            if (!CanProcessVotes())
            {
                player.ChatMessage(_lang("StorageUnavailable", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
                return;
            }

            player.ChatMessage(_lang("EarnRewardAutomatic", player.UserIDString));
            ScheduleVoteFollowUpCheck(player);
        }

        [ChatCommand("claim")]
        private void ClaimChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !configurationLoaded)
            {
                return;
            }

            int remainingSeconds;
            if (IsCommandOnCooldown(player, command, out remainingSeconds))
            {
                player.ChatMessage(_lang("CommandCooldown", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix], remainingSeconds));
                return;
            }

            if (!CanProcessVotes())
            {
                player.ChatMessage(_lang("StorageUnavailable", player.UserIDString, _config.PluginSettings[ConfigDefaultKeys.Prefix]));
                return;
            }

            CheckIfPlayerDataExists(player);
            DeliverPendingRewards(player);

            _Debug("------------------------------");
            _Debug("Method: ClaimChatCommand");
            if (debugEnabled)
            {
                _Debug($"Player: {player.displayName}/{player.UserIDString}");
            }

            // Keep /claim available for manual checks alongside automatic claims.
            CheckVotingStatus(player);
        }

        [ConsoleCommand("eve.pending")]
        private void PendingClaimsConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!EnsureServerConsoleCommand(arg, "eve.pending"))
            {
                return;
            }

            if (arg != null && arg.Args != null && arg.Args.Length > 1)
            {
                ReplyConsoleLocalized(arg, "ConsolePendingUsage");
                return;
            }

            string steamId = null;
            string label;
            if (arg != null && arg.Args != null && arg.Args.Length == 1 && !TryResolveConsoleTarget(arg.GetString(0), out steamId, out label))
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", arg.GetString(0));
                return;
            }

            List<PendingClaimTransaction> transactions = pendingClaimTransactions.Values.Where(transaction => steamId == null || transaction.PlayerId.ToString() == steamId).OrderBy(transaction => transaction.CreatedUtcTicks).ToList();
            if (transactions.Count == 0)
            {
                ReplyConsoleLocalized(arg, "ConsolePendingNone");
                return;
            }

            ReplyConsoleLocalized(arg, "ConsolePendingHeader", transactions.Count);
            foreach (PendingClaimTransaction transaction in transactions.Take(50))
            {
                ReplyConsoleLocalized(arg, "ConsolePendingRow", transaction.TransactionId, transaction.PlayerId, transaction.ServerName, transaction.Site, transaction.State, transaction.Attempts, transaction.ReviewReason ?? "-");
            }

            if (transactions.Count > 50)
            {
                ReplyConsoleLocalized(arg, "ConsolePendingTruncated");
            }
        }

        [ConsoleCommand("eve.resolve")]
        private void ResolveClaimConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!EnsureServerConsoleCommand(arg, "eve.resolve"))
            {
                return;
            }

            if (arg == null || arg.Args == null || arg.Args.Length != 2)
            {
                ReplyConsoleLocalized(arg, "ConsoleResolveUsage");
                return;
            }

            string id = arg.GetString(0);
            string action = arg.GetString(1).ToLowerInvariant();
            if (action != "retry" && action != "grant" && action != "discard")
            {
                ReplyConsoleLocalized(arg, "ConsoleResolveUsage");
                return;
            }

            KeyValuePair<string, PendingClaimTransaction> entry = pendingClaimTransactions.FirstOrDefault(candidate => string.Equals(candidate.Value.TransactionId, id, StringComparison.OrdinalIgnoreCase));
            if (entry.Value == null)
            {
                ReplyConsoleLocalized(arg, "ConsoleTransactionNotFound", id);
                return;
            }

            PendingClaimTransaction transaction = entry.Value;
            if (transaction.State != ClaimStateManualReview || activeClaimRequests.ContainsKey(entry.Key))
            {
                ReplyConsoleLocalized(arg, "ConsoleResolveNotReview", transaction.TransactionId);
                return;
            }

            scheduledClaimRetries.Remove(entry.Key);
            if (action == "discard")
            {
                if (!CloseClaimTransaction(entry.Key, transaction))
                {
                    ReplyConsoleLocalized(arg, "ConsoleOperationFailed");
                    return;
                }
            }
            else if (action == "grant")
            {
                ConfirmClaimTransaction(entry.Key, transaction);
            }
            else
            {
                transaction.Attempts = 0;
                transaction.RecoveryAttempts = 0;
                transaction.NextAttemptUtcTicks = 0;
                transaction.NeedsStatusCheck = transaction.HadAmbiguousFailure;
                transaction.State = transaction.HadAmbiguousFailure ? ClaimStateUncertain : ClaimStateCreated;
                transaction.ReviewReason = null;
                if (SavePendingClaimsData())
                {
                    QueuePendingClaimTransaction(entry.Key);
                }
                else
                {
                    ScheduleClaimRetry(entry.Key);
                }
            }

            ReplyConsoleLocalized(arg, "ConsoleResolutionRequested", transaction.TransactionId, action);
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

            string steamId;
            string targetLabel;
            if (!TryResolveConsoleTarget(arg.GetString(0), out steamId, out targetLabel))
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", arg.GetString(0));
                return;
            }

            if (!BeginVoteReset(steamId))
            {
                ReplyConsoleLocalized(arg, "ConsoleOperationFailed");
                return;
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

            ReplyConsoleLocalized(arg, "ConsoleCheckVoteSuccess", targetLabel, getPlayerVotes(steamId));
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

            string steamId;
            string targetLabel;
            if (!TryResolveConsoleTarget(arg.GetString(0), out steamId, out targetLabel))
            {
                ReplyConsoleLocalized(arg, "ConsolePlayerNotFound", arg.GetString(0));
                return;
            }

            int voteCount;
            if (!int.TryParse(arg.GetString(1), out voteCount) || voteCount < 0)
            {
                ReplyConsoleLocalized(arg, "ConsoleInvalidVoteCount", arg.GetString(1));
                return;
            }

            if (pendingClaimTransactions.Values.Any(transaction => transaction.PlayerId.ToString() == steamId && transaction.TargetVoteCount > 0 && transaction.State != ClaimStateClosed))
            {
                ReplyConsoleLocalized(arg, "ConsolePendingCountChange", targetLabel);
                return;
            }

            if (!TrySetStoredVoteCount(steamId, voteCount))
            {
                ReplyConsoleLocalized(arg, "ConsoleOperationFailed");
                return;
            }

            CancelStatusRequestsForPlayer(steamId);
            ReplyConsoleLocalized(arg, "ConsoleSetVoteSuccess", targetLabel, voteCount);
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

            int resetPlayers = DataFile.Count();
            if (!ResetAllVoteData())
            {
                ReplyConsoleLocalized(arg, "ConsoleOperationFailed");
                return;
            }

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

            if (targetInput.Length >= 2 && ((targetInput[0] == '"' && targetInput[targetInput.Length - 1] == '"') || (targetInput[0] == '\'' && targetInput[targetInput.Length - 1] == '\'')))
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

                BasePlayer knownPlayer = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
                targetLabel = knownPlayer != null ? FormatPlayerForConsole(knownPlayer) : steamId;

                return true;
            }

            List<BasePlayer> exactMatches = BasePlayer.activePlayerList
                .Concat(BasePlayer.sleepingPlayerList)
                .Where(player => player != null && !string.IsNullOrEmpty(player.displayName) && player.displayName.Equals(targetInput, StringComparison.OrdinalIgnoreCase))
                .GroupBy(player => player.userID)
                .Select(group => group.First())
                .ToList();

            BasePlayer matchedPlayer = exactMatches.Count == 1 ? exactMatches[0] : null;

            if (matchedPlayer == null && exactMatches.Count == 0)
            {
                List<BasePlayer> partialMatches = BasePlayer.activePlayerList
                    .Concat(BasePlayer.sleepingPlayerList)
                    .Where(player => player != null && !string.IsNullOrEmpty(player.displayName) && player.displayName.IndexOf(targetInput, StringComparison.OrdinalIgnoreCase) >= 0)
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
            if (player != null)
            {
                if (configurationLoaded)
                {
                    ReplyPlayerConsoleLocalized(player, "ConsoleServerOnly", command);
                }
                else
                {
                    player.SendConsoleCommand("echo", "This command is only available in the server console or RCON.");
                }

                return false;
            }

            if (!CanProcessVotes())
            {
                string message = "Voting is paused because its configuration, data or reset journal could not be loaded safely. Check the server log and reload after repairing it.";
                if (arg != null)
                {
                    arg.ReplyWith(message);
                }
                else
                {
                    ConsoleLog(message);
                }

                return false;
            }

            return true;
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

        protected override void SaveConfig()
        {
            if (Config == null || _config == null || !TryWriteJson(Config.Filename, _config))
            {
                throw new IOException("The configuration could not be saved safely.");
            }
        }

        private string _lang(string key, string id = null, params object[] args)
        {
            string message = lang.GetMessage(key, this, id);

            try
            {
                string formattedMessage = string.Format(message, args);

                // Trim leading whitespace when the first formatting argument is empty.
                if (args != null && args.Length > 0 && string.IsNullOrWhiteSpace(args[0]?.ToString()))
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
                [ConfigDefaultKeys.apiClaim] = "https://rustserverlist.com/api/vote?action=claim&key={0}&steamid={1}",
                [ConfigDefaultKeys.apiStatus] = "https://rustserverlist.com/api/vote?action=status&key={0}&steamid={1}",
                [ConfigDefaultKeys.apiLink] = "https://rustserverlist.com/server/{0}",
                [ConfigDefaultKeys.apiUsername] = "false"
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
                [ConfigDefaultKeys.DebugEnabled] = "false",
                [ConfigDefaultKeys.VerboseDebugEnabled] = "false",
                [ConfigDefaultKeys.CheckAPIResponseCode] = "0",
                [ConfigDefaultKeys.ClaimAPIRepsonseCode] = "0"
            });

            if (_config.PluginSettings == null)
            {
                _config.PluginSettings = new Dictionary<string, string>();
                configChanged = true;
            }

            configChanged |= AddMissingSettings(_config.PluginSettings, new Dictionary<string, string>
            {
                [ConfigDefaultKeys.LogEnabled] = "true",
                [ConfigDefaultKeys.ClearRewardsOnWipe] = "false",
                [ConfigDefaultKeys.RewardIsCumulative] = "false",
                [ConfigDefaultKeys.Prefix] = "<color=#e67e22>[EasyVote]</color>"
            });

            if (_config.NotificationSettings == null)
            {
                _config.NotificationSettings = new Dictionary<string, string>();
                configChanged = true;
            }

            configChanged |= AddMissingSettings(_config.NotificationSettings, new Dictionary<string, string>
            {
                [ConfigDefaultKeys.GlobalChatAnnouncements] = "true",
                [ConfigDefaultKeys.PleaseWaitMessage] = "true",
                [ConfigDefaultKeys.OnPlayerSleepEnded] = "false",
                [ConfigDefaultKeys.OnPlayerConnected] = "true",
                [ConfigDefaultKeys.AutomaticVoteCheckInterval] = DefaultAutomaticVoteCheckInterval.ToString(),
                [ConfigDefaultKeys.VoteFollowUpCheckDelay] = DefaultVoteFollowUpCheckDelay.ToString()
            });

            if (_config.Discord == null)
            {
                _config.Discord = new Dictionary<string, string>();
                configChanged = true;
            }

            configChanged |= AddMissingSettings(_config.Discord, new Dictionary<string, string>
            {
                [ConfigDefaultKeys.discordWebhookURL] = "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
                [ConfigDefaultKeys.DiscordEnabled] = "false",
                [ConfigDefaultKeys.discordTitle] = "A player has just voted for us!"
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

                bool usesUsernameEndpoint = topGamesConfiguration.TryGetValue(ConfigDefaultKeys.apiClaim, out claimUrl) && topGamesConfiguration.TryGetValue(ConfigDefaultKeys.apiStatus, out statusUrl) &&
                    ((claimUrl != null && claimUrl.IndexOf("playername={1}", StringComparison.OrdinalIgnoreCase) >= 0) || (statusUrl != null && statusUrl.IndexOf("playername={1}", StringComparison.OrdinalIgnoreCase) >= 0));

                string usernameSetting;
                bool usernameEnabled = topGamesConfiguration.TryGetValue(ConfigDefaultKeys.apiUsername, out usernameSetting) && string.Equals(usernameSetting, "true", StringComparison.OrdinalIgnoreCase);

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
            if (Config != null && File.Exists(Config.Filename))
            {
                throw new InvalidOperationException("An existing configuration will not be overwritten with defaults.");
            }

            _config = new PluginConfig();
            _config.ConfigurationVersion = CurrentConfigurationVersion;

            _config.DebugSettings = new Dictionary<string, string>
            {
                [ConfigDefaultKeys.DebugEnabled] = "false",
                [ConfigDefaultKeys.VerboseDebugEnabled] = "false",
                [ConfigDefaultKeys.CheckAPIResponseCode] = "0",
                [ConfigDefaultKeys.ClaimAPIRepsonseCode] = "0"
            };

            _config.PluginSettings = new Dictionary<string, string>
            {
                [ConfigDefaultKeys.LogEnabled] = "true",
                [ConfigDefaultKeys.ClearRewardsOnWipe] = "false",
                [ConfigDefaultKeys.RewardIsCumulative] = "false",
                [ConfigDefaultKeys.Prefix] = "<color=#e67e22>[EasyVote]</color> "
            };

            _config.NotificationSettings = new Dictionary<string, string>
            {
                [ConfigDefaultKeys.GlobalChatAnnouncements] = "true",
                [ConfigDefaultKeys.PleaseWaitMessage] = "true",
                [ConfigDefaultKeys.OnPlayerSleepEnded] = "false",
                [ConfigDefaultKeys.OnPlayerConnected] = "true",
                [ConfigDefaultKeys.AutomaticVoteCheckInterval] = DefaultAutomaticVoteCheckInterval.ToString(),
                [ConfigDefaultKeys.VoteFollowUpCheckDelay] = DefaultVoteFollowUpCheckDelay.ToString()
            };

            _config.Discord = new Dictionary<string, string>
            {
                [ConfigDefaultKeys.discordWebhookURL] = "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
                [ConfigDefaultKeys.DiscordEnabled] = "false",
                [ConfigDefaultKeys.discordTitle] = "A player has just voted for us!"
            };

            _config.Rewards = new Dictionary<string, List<string>>
            {
                ["@"] = new List<string>()
                {
                    "inventory.giveto {playerid} supply.signal 1"
                },
                ["first"] = new List<string>()
                {
                    "inventory.giveto {playerid} stones 10000",
                    "sr add {playerid} 10000"
                },
                ["3"] = new List<string>()
                {
                    "addgroup {playerid} vip 7d"
                },
                ["6"] = new List<string>()
                {
                    "grantperm {playerid} plugin.test 1d"
                },
                ["10"] = new List<string>()
                {
                    "zl.lvl {playerid} * 2"
                }
            };

            _config.RewardDescriptions = new Dictionary<string, string>
            {
                ["@"] = "1 Supply Signal",
                ["first"] = "10000 Stones, 10000 RP",
                ["3"] = "7 days of VIP rank",
                ["6"] = "1 day of plugin.test permission",
                ["10"] = "2 zLevels in Every Category"
            };

            _config.Servers = new Dictionary<string, Dictionary<string, string>>
            {
                ["ServerName1"] = new Dictionary<string, string>()
                {
                    ["Rust-Servers.net"] = "ID:KEY",
                    ["Rustservers.gg"] = "ID:KEY",
                    ["BestServers.com"] = "ID:KEY",
                    ["GamesFinder.net"] = "ID:KEY",
                    ["Top-Games.net"] = "ID:KEY",
                    ["TrackyServer.com"] = "ID:KEY",
                    ["RustServerList.com"] = "ID:KEY"
                },
                ["ServerName2"] = new Dictionary<string, string>()
                {
                    ["Rust-Servers.net"] = "ID:KEY",
                    ["Rustservers.gg"] = "ID:KEY",
                    ["BestServers.com"] = "ID:KEY",
                    ["GamesFinder.net"] = "ID:KEY",
                    ["Top-Games.net"] = "ID:KEY",
                    ["TrackyServer.com"] = "ID:KEY",
                    ["RustServerList.com"] = "ID:KEY"
                }
            };

            _config.ServersCustomLink = new Dictionary<string, string>
            {
                ["ServerName1"] = "https://vote.servername1.com"
            };

            _config.VoteSitesAPI = new Dictionary<string, Dictionary<string, string>>
            {
                ["Rust-Servers.net"] = new Dictionary<string, string>()
                {
                    [ConfigDefaultKeys.apiClaim] = "https://rust-servers.net/api/?action=custom&object=plugin&element=reward&key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiStatus] = "https://rust-servers.net/api/?object=votes&element=claim&key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiLink] = "https://rust-servers.net/server/{0}",
                    [ConfigDefaultKeys.apiUsername] = "false"
                },
                ["Rustservers.gg"] = new Dictionary<string, string>()
                {
                    [ConfigDefaultKeys.apiClaim] = "https://rustservers.gg/vote-api.php?action=claim&key={0}&server={2}&steamid={1}",
                    [ConfigDefaultKeys.apiStatus] = "https://rustservers.gg/vote-api.php?action=status&key={0}&server={2}&steamid={1}",
                    [ConfigDefaultKeys.apiLink] = "https://rustservers.gg/server/{0}",
                    [ConfigDefaultKeys.apiUsername] = "false"
                },
                ["BestServers.com"] = new Dictionary<string, string>()
                {
                    [ConfigDefaultKeys.apiClaim] = "https://bestservers.com/api/vote.php?action=claim&key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiStatus] = "https://bestservers.com/api/vote.php?action=status&key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiLink] = "https://bestservers.com/server/{0}",
                    [ConfigDefaultKeys.apiUsername] = "false"
                },
                ["GamesFinder.net"] = new Dictionary<string, string>()
                {
                    [ConfigDefaultKeys.apiClaim] = "https://www.gamesfinder.net/api/vote?mode=claim&key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiStatus] = "https://www.gamesfinder.net/api/vote?key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiLink] = "https://www.gamesfinder.net/server/{0}",
                    [ConfigDefaultKeys.apiUsername] = "false"
                },
                ["Top-Games.net"] = new Dictionary<string, string>()
                {
                    [ConfigDefaultKeys.apiClaim] = "https://api.top-games.net/v1/votes/claim-username?server_token={0}&playername={1}",
                    [ConfigDefaultKeys.apiStatus] = "https://api.top-games.net/v1/votes/check?server_token={0}&playername={1}",
                    [ConfigDefaultKeys.apiLink] = "https://top-games.net/rust/{0}",
                    [ConfigDefaultKeys.apiUsername] = "true"
                },
                ["TrackyServer.com"] = new Dictionary<string, string>()
                {
                    [ConfigDefaultKeys.apiClaim] = "https://api.trackyserver.com/vote/?action=claim&key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiStatus] = "https://api.trackyserver.com/vote/?action=status&key={0}&steamid={1}",
                    [ConfigDefaultKeys.apiLink] = "https://trackyserver.com/server/{0}",
                    [ConfigDefaultKeys.apiUsername] = "false"
                },
                ["RustServerList.com"] = GetRustServerListApiConfiguration()
            };

            SaveConfig();
            ConsoleWarn("A new configuration file has been generated!");
        }

        protected override void LoadConfig()
        {
            configurationLoaded = false;

            try
            {
                base.LoadConfig();
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                {
                    throw new InvalidDataException("The configuration root is null.");
                }

                EnsureConfigDefaults();
                configurationLoaded = true;
            }
            catch (Exception exception)
            {
                if (Config != null)
                {
                    PreserveUnreadableFile(Config.Filename);
                }

                _config = null;
                ConsoleError($"Configuration loading failed ({exception.GetType().Name}). The original file was preserved. Repair it and reload the plugin.");
            }
        }

        private void LoadMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["CommandCooldown"] = "{0} Please wait <color=#e67e22>{1}</color> second(s) before using this command again.",
                ["PendingRewardsDelivered"] = "{0} Your pending vote reward(s) have been delivered!",
                ["ThankYouPending"] = "{0} Thank you for voting on <color=#e67e22>{2}</color>! You now have <color=#e67e22>{1}</color> vote(s). Your reward is pending and will be delivered when you are fully connected.",
                ["DiscordWebhookMessagePending"] = "{0} has voted for {1} on {2}. The reward is pending and will be delivered when the player reconnects or wakes up.",
                ["GlobalChatAnnouncementsPending"] = "{0} <color=#e67e22>{1}</color> has voted <color=#e67e22>{2}</color> time(s). Their reward is pending and will be delivered when they are fully connected.",
                ["ClaimStatus"] = "{0} <color=#e67e22>{1}</color> reports you have not voted yet on <color=#e67e22>{2}</color>. Vote now!",
                ["PleaseWait"] = "{0} Checking all the vote sites API's... Please be patient as this can take some time...",
                ["VoteList"] = "{0} You can vote for our server at the following links:",
                ["EarnRewardAutomatic"] = "Your reward will be claimed automatically while you are online or the next time you connect!",
                ["ThankYou"] = "{0} Thank you for voting on <color=#e67e22>{2}</color>! You now have <color=#e67e22>{1}</color> vote(s). Your reward has been delivered!",
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
                ["ConsoleResetVoteDataSuccess"] = "Vote data has been reset for {0} player(s).",
                ["StorageUnavailable"] = "{0} Voting is temporarily unavailable because its data could not be loaded safely. Please contact an administrator.",
                ["ClaimNeedsReview"] = "{0} Your pending vote on {1} needs administrator review before another claim can be processed.",
                ["ConsoleOperationFailed"] = "The operation could not be completed safely. Check the server log before retrying.",
                ["ConsolePendingCountChange"] = "Cannot change the counter for {0} while a confirmed transaction is unfinished. Resolve it first, or use eve.clearvote to explicitly clear pending data.",
                ["ConsolePendingUsage"] = "Usage: eve.pending [steamid|username]",
                ["ConsolePendingNone"] = "No pending vote transactions match this request.",
                ["ConsolePendingHeader"] = "Pending vote transactions: {0}",
                ["ConsolePendingRow"] = "{0} | player={1} | server={2} | site={3} | state={4} | attempts={5} | reason={6}",
                ["ConsolePendingTruncated"] = "Only the first 50 entries are shown. Use eve.pending <steamid> to filter.",
                ["ConsoleResolveUsage"] = "Usage: eve.resolve <transactionid> <retry|grant|discard>. Grant records and rewards the vote locally; it does not contact the tracker. Use it only after verification.",
                ["ConsoleTransactionNotFound"] = "No pending vote transaction matches '{0}'.",
                ["ConsoleResolveNotReview"] = "Transaction {0} is not awaiting manual review or still has a request in progress.",
                ["ConsoleResolutionRequested"] = "Resolution '{1}' requested for transaction {0}. Use eve.pending to check any remaining work.",
                ["ThankYouDispatchFailed"] = "{0} Your vote on {2} was recorded. A reward command failed during dispatch; please contact an administrator.",
                ["DiscordWebhookMessageFailed"] = "{0} voted for {1} on {2}, but a reward command failed during dispatch. Administrator review is required.",
                ["GlobalChatAnnouncementsFailed"] = "{0} {1} has voted {2} time(s). Their reward needs administrator review."
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["CommandCooldown"] = "{0} Te rugăm să aștepți <color=#e67e22>{1}</color> secunde înainte de a folosi din nou această comandă.",
                ["PendingRewardsDelivered"] = "{0} Recompensele de vot aflate în așteptare au fost acordate!",
                ["ThankYouPending"] = "{0} Mulțumim pentru votul pe <color=#e67e22>{2}</color>! Ai acum <color=#e67e22>{1}</color> voturi. Recompensa este în așteptare și va fi acordată după ce ești conectat complet.",
                ["DiscordWebhookMessagePending"] = "{0} a votat pentru {1} pe {2}. Recompensa este în așteptare și va fi acordată atunci când jucătorul se reconectează sau se trezește.",
                ["GlobalChatAnnouncementsPending"] = "{0} <color=#e67e22>{1}</color> a votat de <color=#e67e22>{2}</color> ori. Recompensa este în așteptare și va fi acordată când jucătorul este conectat complet.",
                ["ClaimStatus"] = "{0} <color=#e67e22>{1}</color> raportează că nu ai votat încă pe <color=#e67e22>{2}</color>. Votează acum!",
                ["PleaseWait"] = "{0} Se verifică toate API-urile site-urilor de vot... Te rugăm să ai răbdare, acest proces poate dura ceva timp...",
                ["VoteList"] = "{0} Poți vota pentru server-ul nostru accesând următoarele link-uri:",
                ["EarnRewardAutomatic"] = "Recompensa va fi revendicată automat cât timp ești online sau data viitoare când intri pe server!",
                ["ThankYou"] = "{0} Mulțumim pentru votul pe <color=#e67e22>{2}</color>! Ai acum <color=#e67e22>{1}</color> voturi. Recompensa a fost acordată!",
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
                ["ConsoleResetVoteDataSuccess"] = "Datele de vot au fost resetate pentru {0} jucător(i).",
                ["StorageUnavailable"] = "{0} Sistemul de vot este temporar indisponibil deoarece datele nu au putut fi încărcate în siguranță. Contactează un administrator.",
                ["ClaimNeedsReview"] = "{0} Votul tău în așteptare pe {1} necesită verificarea unui administrator înaintea unei noi revendicări.",
                ["ConsoleOperationFailed"] = "Operația nu a putut fi finalizată în siguranță. Verifică jurnalul serverului înainte de a reîncerca.",
                ["ConsolePendingCountChange"] = "Contorul pentru {0} nu poate fi modificat cât timp există o tranzacție confirmată nefinalizată. Rezolv-o mai întâi sau folosește eve.clearvote pentru a șterge explicit datele în așteptare.",
                ["ConsolePendingUsage"] = "Utilizare: eve.pending [steamid|nume]",
                ["ConsolePendingNone"] = "Nu există tranzacții de vot în așteptare pentru această cerere.",
                ["ConsolePendingHeader"] = "Tranzacții de vot în așteptare: {0}",
                ["ConsolePendingRow"] = "{0} | jucător={1} | server={2} | site={3} | stare={4} | încercări={5} | motiv={6}",
                ["ConsolePendingTruncated"] = "Sunt afișate primele 50 de intrări. Folosește eve.pending <steamid> pentru filtrare.",
                ["ConsoleResolveUsage"] = "Utilizare: eve.resolve <transactionid> <retry|grant|discard>. Grant înregistrează și recompensează votul local, fără a contacta site-ul. Folosește-l doar după verificare.",
                ["ConsoleTransactionNotFound"] = "Nu există o tranzacție de vot în așteptare pentru '{0}'.",
                ["ConsoleResolveNotReview"] = "Tranzacția {0} nu așteaptă verificare manuală sau are încă o cerere în desfășurare.",
                ["ConsoleResolutionRequested"] = "Rezolvarea '{1}' a fost solicitată pentru tranzacția {0}. Verifică operațiile rămase cu eve.pending.",
                ["ThankYouDispatchFailed"] = "{0} Votul tău pe {2} a fost înregistrat. O comandă de recompensă a eșuat la executare; contactează un administrator.",
                ["DiscordWebhookMessageFailed"] = "{0} a votat pentru {1} pe {2}, dar o comandă de recompensă a eșuat la executare. Este necesară verificarea unui administrator.",
                ["GlobalChatAnnouncementsFailed"] = "{0} {1} a votat de {2} ori. Recompensa necesită verificarea unui administrator."
            }, this, "ro");
        }

        ////////////////////////////////////////////////////////////
        // Files
        ////////////////////////////////////////////////////////////

        private string GetDataPath(string name)
        {
            return Interface.Oxide.DataFileSystem.GetFile(name).Filename;
        }

        private bool LoadVoteData()
        {
            Dictionary<string, object> loaded;
            if (!TryReadJson("EasyVoteExtended", out loaded))
            {
                return false;
            }

            DataFile = new DynamicConfigFile(GetDataPath("EasyVoteExtended"));
            foreach (KeyValuePair<string, object> entry in loaded)
            {
                ulong playerId;
                int count;
                if (!ulong.TryParse(entry.Key, out playerId) || !IsValidSteamId(playerId) || entry.Value == null || !int.TryParse(entry.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 0)
                {
                    ReportInvalidData(DataFile.Filename, "Invalid player ID or vote counter.");
                    return false;
                }

                DataFile[entry.Key] = count;
            }

            return true;
        }

        private bool TrySetStoredVoteCount(string steamId, int count)
        {
            if (!dataLoaded || DataFile == null || count < 0)
            {
                return false;
            }

            object previous = DataFile[steamId];
            DataFile[steamId] = count;
            if (SaveDataFile(DataFile))
            {
                return true;
            }

            if (previous == null)
            {
                DataFile.Remove(steamId);
            }
            else
            {
                DataFile[steamId] = previous;
            }

            return false;
        }

        private bool TryReadJson<T>(string name, out T result) where T : class, new()
        {
            result = null;
            string path = GetDataPath(name);

            try
            {
                if (!File.Exists(path))
                {
                    if (File.Exists(path + ".bak"))
                    {
                        throw new InvalidDataException("The primary data file is missing but its backup exists.");
                    }

                    result = new T();
                    return true;
                }

                result = JsonConvert.DeserializeObject<T>(File.ReadAllText(path), new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None
                });
                if (result == null)
                {
                    throw new InvalidDataException("The data root is null.");
                }

                return true;
            }
            catch (Exception exception)
            {
                ReportInvalidData(path, $"Loading failed: {exception.GetType().Name}.");
                return false;
            }
        }

        private bool TryWriteJson(string path, object value)
        {
            string temporaryPath = path + ".tmp";

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                byte[] json = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented, new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None
                }));
                using (FileStream stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(json, 0, json.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    // Never fall back to deleting the original file before replacement.
                    File.Replace(temporaryPath, path, path + ".bak");
                }
                else
                {
                    File.Move(temporaryPath, path);
                }

                storageWarningTimes.Remove(path);
                return true;
            }
            catch (Exception exception)
            {
                long lastWarning;
                long now = DateTime.UtcNow.Ticks;
                if (!storageWarningTimes.TryGetValue(path, out lastWarning) || now - lastWarning >= 30L * TimeSpan.TicksPerSecond)
                {
                    storageWarningTimes[path] = now;
                    ConsoleError($"Could not save {Path.GetFileName(path)} safely ({exception.GetType().Name}). Check free disk space, permissions and atomic file replacement support.");
                }

                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch (Exception)
                {
                    // A stale temporary file is never used as authoritative data.
                }
            }
        }

        private void ReportInvalidData(string path, string reason)
        {
            PreserveUnreadableFile(path);
            ConsoleError($"Cannot load {Path.GetFileName(path)}: {reason} The original was preserved; repair it and reload the plugin.");
        }

        private void PreserveUnreadableFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string suffix = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
                    File.Copy(path, path + ".invalid-" + suffix, false);
                }
            }
            catch (Exception)
            {
                // Leave the original untouched even if a diagnostic copy cannot be made.
            }
        }

        private bool RecoverResetJournal()
        {
            VoteResetJournal journal;
            if (!TryReadJson(ResetJournalDataFileName, out journal))
            {
                resetInProgress = true;
                return false;
            }

            if (string.IsNullOrEmpty(journal.OperationId) && !journal.AllPlayers && journal.SteamId == null)
            {
                return true;
            }

            Guid operationId;
            ulong playerId;
            if (!Guid.TryParse(journal.OperationId, out operationId) || (!journal.AllPlayers && (!ulong.TryParse(journal.SteamId, out playerId) || !IsValidSteamId(playerId))))
            {
                resetInProgress = true;
                ReportInvalidData(GetDataPath(ResetJournalDataFileName), "Invalid reset journal.");
                return false;
            }

            ConsoleWarn("Resuming an interrupted vote reset before processing new votes.");
            return ApplyVoteReset(journal);
        }

        private bool BeginVoteReset(string steamId)
        {
            if (!CanProcessVotes())
            {
                return false;
            }

            VoteResetJournal journal = new VoteResetJournal
            {
                OperationId = Guid.NewGuid().ToString("N"),
                SteamId = steamId,
                AllPlayers = steamId == null
            };

            pendingResetJournal = journal;
            resetInProgress = true;
            if (!TryWriteJson(GetDataPath(ResetJournalDataFileName), journal))
            {
                ConsoleError("The reset intent could not be saved. Voting is paused and the operation will be retried. Do not restart before storage recovers; an unsaved reset cannot be recovered after a process crash.");
                return false;
            }

            return ApplyVoteReset(journal);
        }

        private bool ApplyVoteReset(VoteResetJournal journal)
        {
            resetInProgress = true;
            pendingResetJournal = journal;
            runtimeGeneration++;

            // Invalidate all callbacks; unrelated in-flight claims will be reconciled.
            activeClaimRequests.Clear();
            pendingStatusChecks.Clear();
            statusRequestTokens.Clear();
            scheduledClaimRetries.Clear();
            claimRequestQueue.Clear();
            voteRequestQueue.Clear();
            consecutiveClaimRequests = 0;
            foreach (PendingClaimTransaction transaction in pendingClaimTransactions.Values)
            {
                if (transaction.State == ClaimStateSent)
                {
                    transaction.State = ClaimStateUncertain;
                    transaction.HadAmbiguousFailure = true;
                    transaction.NeedsStatusCheck = true;
                }
            }

            if (journal.AllPlayers)
            {
                foreach (KeyValuePair<string, object> entry in DataFile.ToArray())
                {
                    DataFile[entry.Key] = 0;
                }

                pendingRewardCommands.Clear();
                pendingClaimTransactions.Clear();
                scheduledVoteChecks.Clear();
                commandCooldowns.Clear();
            }
            else
            {
                DataFile[journal.SteamId] = 0;
                pendingRewardCommands.Remove(journal.SteamId);
                ulong playerId = ulong.Parse(journal.SteamId, CultureInfo.InvariantCulture);
                scheduledVoteChecks.Remove(playerId);
                foreach (KeyValuePair<string, PendingClaimTransaction> entry in pendingClaimTransactions.Where(entry => entry.Value.PlayerId == playerId).ToArray())
                {
                    pendingClaimTransactions.Remove(entry.Key);
                }
            }

            if (!SaveDataFile(DataFile) || !SavePendingRewardsData() || !SavePendingClaimsData() || !TryWriteJson(GetDataPath(ResetJournalDataFileName), new VoteResetJournal()))
            {
                ConsoleError("The vote reset could not finish. Voting is paused while storage recovery is retried. The saved reset journal also resumes the operation after reload.");
                return false;
            }

            pendingResetJournal = null;
            resetInProgress = false;
            RecoverPendingClaimTransactions();
            ConsoleLog("The requested vote reset has completed safely.");
            return true;
        }

        protected internal static DynamicConfigFile DataFile;

        private bool SaveDataFile(DynamicConfigFile data)
        {
            if (!dataLoaded || data == null)
            {
                return false;
            }

            return TryWriteJson(data.Filename, data.ToDictionary(entry => entry.Key, entry => entry.Value));
        }

        private bool LoadPendingRewardsData()
        {
            Dictionary<string, List<string>> loaded;
            if (!TryReadJson(PendingRewardsDataFileName, out loaded))
            {
                return false;
            }

            foreach (KeyValuePair<string, List<string>> entry in loaded)
            {
                ulong playerId;
                if (!ulong.TryParse(entry.Key, out playerId) || !IsValidSteamId(playerId) || entry.Value == null)
                {
                    ReportInvalidData(GetDataPath(PendingRewardsDataFileName), "Invalid player ID or reward command list.");
                    return false;
                }
            }

            pendingRewardCommands = loaded;
            return true;
        }

        private bool SavePendingRewardsData()
        {
            return dataLoaded && TryWriteJson(GetDataPath(PendingRewardsDataFileName), pendingRewardCommands);
        }

        private bool LoadPendingClaimsData()
        {
            Dictionary<string, PendingClaimTransaction> loaded;
            if (!TryReadJson(PendingClaimsDataFileName, out loaded))
            {
                return false;
            }

            HashSet<string> transactionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PendingClaimTransaction> entry in loaded)
            {
                PendingClaimTransaction transaction = entry.Value;
                if (transaction == null || !IsValidSteamId(transaction.PlayerId) || string.IsNullOrWhiteSpace(transaction.ServerName) || string.IsNullOrWhiteSpace(transaction.Site) ||
                    entry.Key != GetPendingClaimKey(transaction.PlayerId, transaction.ServerName, transaction.Site))
                {
                    ReportInvalidData(GetDataPath(PendingClaimsDataFileName), "Invalid claim transaction identity.");
                    return false;
                }

                Guid transactionId;
                if (!string.IsNullOrEmpty(transaction.TransactionId) && (!Guid.TryParseExact(transaction.TransactionId, "N", out transactionId) || !transactionIds.Add(transaction.TransactionId)))
                {
                    ReportInvalidData(GetDataPath(PendingClaimsDataFileName), "Invalid or duplicate transaction ID.");
                    return false;
                }

                bool validState = string.IsNullOrEmpty(transaction.State) || transaction.State == ClaimStateCreated || transaction.State == ClaimStateSent ||
                    transaction.State == ClaimStateUncertain || transaction.State == ClaimStateConfirmed || transaction.State == ClaimStateManualReview || transaction.State == ClaimStateClosed;
                if (!validState || transaction.Attempts < 0 || transaction.RecoveryAttempts < 0 || transaction.CreatedUtcTicks < 0 || transaction.CreatedUtcTicks > DateTime.MaxValue.Ticks ||
                    transaction.TargetVoteCount < 0 || transaction.NextAttemptUtcTicks < 0 || transaction.NextAttemptUtcTicks > DateTime.MaxValue.Ticks)
                {
                    ReportInvalidData(GetDataPath(PendingClaimsDataFileName), "Invalid claim transaction state. Do not downgrade while transactions are pending.");
                    return false;
                }

                if (transaction.RewardCommands != null && transaction.RewardCommands.Any(command => IsPendingRewardTransactionMarker(command)))
                {
                    ReportInvalidData(GetDataPath(PendingClaimsDataFileName), "A reward snapshot contains a reserved transaction marker.");
                    return false;
                }
            }

            pendingClaimTransactions = loaded;
            return true;
        }

        private bool SavePendingClaimsData()
        {
            if (!dataLoaded)
            {
                return false;
            }

            bool saved = TryWriteJson(GetDataPath(PendingClaimsDataFileName), pendingClaimTransactions);
            claimsDirty = !saved;
            return saved;
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