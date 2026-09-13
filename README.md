# csgoSkinStatistics

A very simple site designed to hit CS2's game coordinator and return information about CS2 items.

### Installation and Setup

After cloning this repository, set the `STEAM_USERNAME` and `STEAM_PASSWORD` environment variables or create a `steam-accounts.json` file following the example in `steam-accounts.json.example` if you need multiple accounts. Run with dotnet. The page will be on localhost:5000.

### Operations

`GET /health` answers 200 while Steam is accepting inventory fetches and at least one bot account is logged in, and 503 with the reason otherwise. Point an external monitor at it.

The knobs an operator is most likely to need are in `appsettings.json` (or an `appsettings.Production.json` next to the published app): `Steam:UserAgent`, the `SteamEgress` pacing, and the `InventorySnapshots` retention.

### API

Endpoint: https://skinstats.app/api

#### Usage

##### Pre-Parsed Inspect Link Elements (preferred)

|Parameter|Description                                   |
|---------|----------------------------------------------|
|s        |Required (used if item from inventory, else 0)|
|a        |Required (inspect link 'a' parameter)         |
|d        |Required (inspect link 'd' parameter)         |
|m        |Required (used if item from market, else 0)   |

Example Inventory Item

`https://skinstats.app/api?s=76561198261551396&a=12256887280&d=2776544801323831695&m=0`

Example Market Item

`https://skinstats.app/api?m=563330426657599553&a=6710760926&d=9406593057029549017&s=0`

##### Full Inspect URL

|Parameter|Description                         |
|---------|------------------------------------|
|url      |Required (full inspect link of item)|

Example Inventory Item

`https://skinstats.app/api?url=steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S76561198261551396A12256887280D2776544801323831695`

Example Market Item

`https://skinstats.app/api?url=steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20M625254122282020305A6760346663D30614827701953021`

#### Response

|Attribute |Type   |Description                                    |
|----------|-------|-----------------------------------------------|
|itemid    |integer|Item ID                                        |
|defindex  |integer|Weapon ID                                      |
|paintindex|integer|Numerical ID for the skin                      |
|rarity    |integer|Rarity value                                   |
|quality   |integer|Quality value                                  |
|paintwear |float  |Wear of the skin (0-1)                         |
|paintseed |integer|How the skin texture is placed                 |
|inventory |integer|Inventory ID                                   |
|origin    |integer|Origin of the weapon                           |
|stattrak  |integer|1 if stattrak, 0 if not                        |
|weapon    |string |Name of the weapon                             |
|skin      |string |Name of the skin                               |
|special   |string |Special attributes of the skin (i.e. "Phase 1")|

##### Errors

For right now, errors come in many non-conformed shapes and sizes. Hopefully you don't run into any, but if you do, make sure your input exactly matches the examples. If you still have problems, feel free to open an Issue on GitHub.
