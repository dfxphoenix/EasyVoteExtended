## Overview
**Easy Vote Extended** is an upgraded version of Easy Vote, the most advanced and versatile voting plugin available, designed to enhance player engagement and community interaction effortlessly.

With its fully customizable system, server owners can tailor every aspect of the voting process without any coding knowledge.

## Chat Commands
* ``/vote`` - Show vote link(s).
* ``/claim`` - Claim vote reward(s).
* ``/rewardlist`` - Display what reward(s) can get.

## Server Commands
* ``eve.clearvote`` - Clear a player vote count.
* ``eve.checkvote`` - Check a player vote count.
* ``eve.setvote`` - Set a player vote count to a specific number.
* ``eve.resetvotedata`` - Reset all vote data.

## Configuration
```json
{
  "Debug Settings": {
    "Debug Enabled?": "false",
    "Enable Verbose Debugging?": "false",
    "Set Check API Response Code (0 = Not found, 1 = Has voted and not claimed, 2 = Has voted and claimed)": "0",
    "Set Claim API Response Code (0 = Not found, 1 = Has voted and not claimed. The vote will now be set as claimed., 2 = Has voted and claimed": "0"
  },
  "Plugin Settings": {
    "Enable logging => logs/EasyVoteExtended (true / false)": "true",
    "Wipe Rewards Count on Map Wipe?": "false",
    "Vote rewards cumulative (true / false)": "false",
    "Chat Prefix": "<color=#e67e22>[EasyVote]</color> "
  },
  "Notification Settings": {
    "Globally announcment in chat when player voted (true / false)": "true",
    "Enable the 'Please Wait' message when checking voting status?": "true",
    "Notify player of rewards when they stop sleeping?": "false",
    "Notify player of rewards when they connect to the server?": "true"
  },
  "Discord": {
    "Discord webhook (URL)": "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
    "DiscordMessage Enabled (true / false)": "false",
    "Discord Title": "A player has just voted for us!"
  },
  "Rewards": {
    "@": [
      "giveto {playerid} supply.signal 1"
    ],
    "first": [
      "giveto {playerid} stones 10000",
      "sr add {playerid} 10000"
    ],
    "3": [
      "addgroup {playerid} vip 7d"
    ],
    "6": [
      "grantperm {playerid} plugin.test 1d"
    ],
    "10": [
      "zl.lvl {playerid} * 2"
    ]
  },
  "Reward Descriptions": {
    "@": "1 Supply Signal",
    "first": "10000 Stones, 10000 RP",
    "3": "7 days of VIP rank",
    "6": "1 day of plugin.test permission",
    "10": "2 zLevels in Every Category"
  },
  "Server Voting IDs and Keys": {
    "ServerName1": {
      "Rust-Servers.net": "ID:KEY",
      "Rustservers.gg": "ID:KEY",
      "BestServers.com": "ID:KEY",
      "GamesFinder.net": "ID:KEY",
      "Top-Games.net": "ID:KEY",
      "TrackyServer.com": "ID:KEY"
    },
    "ServerName2": {
      "Rust-Servers.net": "ID:KEY",
      "Rustservers.gg": "ID:KEY",
      "BestServers.com": "ID:KEY",
      "GamesFinder.net": "ID:KEY",
      "Top-Games.net": "ID:KEY",
      "TrackyServer.com": "ID:KEY"
    }
  },
  "Server Vote Custom link": {
    "ServerName1": "https://vote.servername1.com"
  },
  "Voting Sites API Information": {
    "Rust-Servers.net": {
      "API Claim Reward (GET URL)": "https://rust-servers.net/api/?action=custom&object=plugin&element=reward&key={0}&steamid={1}",
      "API Vote status (GET URL)": "https://rust-servers.net/api/?object=votes&element=claim&key={0}&steamid={1}",
      "Vote link (URL)": "https://rust-servers.net/server/{0}",
      "Site Uses Username Instead of Player Steam ID?": "false"
    },
    "Rustservers.gg": {
      "API Claim Reward (GET URL)": "https://rustservers.gg/vote-api.php?action=claim&key={0}&server={2}&steamid={1}",
      "API Vote status (GET URL)": "https://rustservers.gg/vote-api.php?action=status&key={0}&server={2}&steamid={1}",
      "Vote link (URL)": "https://rustservers.gg/server/{0}",
      "Site Uses Username Instead of Player Steam ID?": "false"
    },
    "BestServers.com": {
      "API Claim Reward (GET URL)": "https://bestservers.com/api/vote.php?action=claim&key={0}&steamid={1}",
      "API Vote status (GET URL)": "https://bestservers.com/api/vote.php?action=status&key={0}&steamid={1}",
      "Vote link (URL)": "https://bestservers.com/server/{0}",
      "Site Uses Username Instead of Player Steam ID?": "false"
    },
    "GamesFinder.net": {
      "API Claim Reward (GET URL)": "https://www.gamesfinder.net/api/vote?mode=claim&key={0}&steamid={1}",
      "API Vote status (GET URL)": "https://www.gamesfinder.net/api/vote?key={0}&steamid={1}",
      "Vote link (URL)": "https://www.gamesfinder.net/server/{0}",
      "Site Uses Username Instead of Player Steam ID?": "false"
    },
    "Top-Games.net": {
      "API Claim Reward (GET URL)": "https://api.top-games.net/v1/votes/claim-username?server_token={0}&playername={1}",
      "API Vote status (GET URL)": "https://api.top-games.net/v1/votes/check?server_token={0}&playername={1}",
      "Vote link (URL)": "https://top-games.net/rust/{0}",
      "Site Uses Username Instead of Player Steam ID?": "false"
    },
    "TrackyServer.com": {
      "API Claim Reward (GET URL)": "https://api.trackyserver.com/vote/?action=claim&key={0}&steamid={1}",
      "API Vote status (GET URL)": "https://api.trackyserver.com/vote/?action=status&key={0}&steamid={1}",
      "Vote link (URL)": "https://trackyserver.com/server/{0}",
      "Site Uses Username Instead of Player Steam ID?": "false"
    }
  }
}
```
## Languages
**Easy Vote Extended** have two languages by default (**English** and **Romanian**), but you can add more in Oxide lang folder

## API Hooks
```csharp
int getPlayerVotes(string steamID)
```