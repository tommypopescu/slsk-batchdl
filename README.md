# sldl

An automatic downloader for Soulseek built with Soulseek.NET. Accepts CSV files as well as Spotify and YouTube urls.  
Supports playlist and album downloads; selects the best files according to user-configured file conditions and heuristics.

See the [usage examples](#examples-2).

## Index
 - [Options](#options)
 - [Input types](#input-types)
   - [CSV file](#csv-file)
   - [YouTube](#youtube)
   - [Spotify](#spotify)
   - [Bandcamp](#bandcamp)
   - [Soulseek Link](#soulseek-link)
   - [Search string](#search-string)
   - [List file](#list-file)
 - [Download modes](#download-modes)
   - [Normal](#normal)
   - [Album](#album)
   - [Aggregate](#aggregate)
   - [Album Aggregate](#album-aggregate)
 - [Searching](#searching)
 - [File conditions](#file-conditions)
 - [Name format](#name-format)
 - [On-Complete Actions](#on-complete-actions)
 - [Configuration](#configuration)
 - [Shortcuts \& interactive mode](#shortcuts--interactive-mode)
 - [Examples](#examples-2)
 - [Notes](#notes)
 - [Tips](#notes)
 - [Docker](#docker)


## Options

```
Usage: sldl <input> [OPTIONS]
```
#### Required Arguments
```
<input>                         A url, search string, or path to a local CSV file.
                                Run `--help input` to view the accepted inputs.
                                Can also be passed with -i, --input <input>
--user <username>               Soulseek username
--pass <password>               Soulseek password
```
#### General Options
```
-p, --path <path>               Download directory
--input-type <type>             [csv|youtube|spotify|bandcamp|string|list]
--name-format <format>          Name format for downloaded tracks. See `--help name-format`
    
-n, --number <maxtracks>        Download the first n tracks of a playlist
-o, --offset <offset>           Skip a specified number of tracks
-r, --reverse                   Download tracks in reverse order
-c, --config <path>             Set config file location. Set to 'none' to ignore config
--profile <names>               Configuration profile(s) to use. See `--help config`.
--concurrent-downloads <num>    Max concurrent downloads for normal mode (default: 2)
--write-playlist                Create an m3u playlist file in the output directory
--playlist-path <path>          Override default path for m3u playlist file

--no-skip-existing              Do not skip downloaded tracks
--no-write-index                Do not create a file indexing all downloaded tracks
--index-path <path>             Override default path for sldl index
--skip-check-cond               Check file conditions when skipping existing files
--skip-check-pref-cond          Check preferred conditions when skipping existing files  
--skip-music-dir <path>         Also skip downloading tracks found in a music library by
                                comparing filenames. Not 100% reliable.
--skip-not-found                Skip searching for tracks that weren't found on Soulseek
                                during the last run.
    
--listen-port <port>            Port for incoming connections (default: 49998)
--on-complete <command>         Run a command when a download completes. See `--help
                                on-complete`

-v, --verbose                   Print extra debug info
--log-file <path>               Write debug info to a specified file
--no-progress                   Disable progress bars/percentages, only simple printing
--print <option>                Print tracks or search results instead of downloading:
                                'tracks': Print all tracks to be downloaded
                                'tracks-full': Print extended information about all tracks
                                'results': Print search results satisfying file conditions
                                'results-full': Print search results including full paths.
                                'json': Print first result in json format
                                'json-all': Print json of all results in sorted order
                                'link': Print first result slsk:// link
```
#### Search Options
```
--fast-search                   Begin downloading as soon as a file satisfying the preferred
                                conditions is found. Only for normal download mode.
--remove-ft                     Remove 'feat.' and everything after before searching
--no-remove-special-chars       Do not remove special characters before searching
--remove-brackets               Remove square brackets and their contents before searching
--regex <regex>                 Remove a regexp from all track titles and artist names.
                                Optionally specify a replacement regex after a semicolon.
                                Add 'T:', 'A:' or 'L:' at the start to only apply this to
                                the track title, artist, or album respectively.
--artist-maybe-wrong            Performs an additional search without the artist name.
                                Useful for sources like SoundCloud where the "artist"
                                could just be an uploader. Note that when downloading a
                                YouTube playlist via url, this option is set automatically
                                on a per-track basis, so it is best kept off in that case.
-d, --desperate                 Tries harder to find the desired track by searching for the
                                artist/album/title only, then filtering. (slower search)
--fails-to-downrank <num>       Number of fails to downrank a user's shares (default: 1)
--fails-to-ignore <num>         Number of fails to ban/ignore a user's shares (default: 2)

--yt-dlp                        Use yt-dlp to download tracks that weren't found on
                                Soulseek. yt-dlp must be available from the command line.
--yt-dlp-argument <str>         The command line arguments when running yt-dlp. Default:
                                "{id}" -f bestaudio/best -cix -o "{savepath}.%(ext)s"
                                Available vars are: {id}, {savedir}, {savepath} (w/o ext).
                                Note that -x causes yt-dlp to download webms in case ffmpeg
                                is unavailable.

--search-timeout <ms>           Max search time in ms (default: 6000)
--max-stale-time <ms>           Max download time without progress in ms (default: 30000)
--searches-per-time <num>       Max searches per time interval. Higher values may cause
                                30-minute bans, see `--help search`. (default: 34)
--searches-renew-time <sec>     Controls how often available searches are replenished.
                                See `--help search`. (default: 220)
```
#### Spotify Options
```
--spotify-id <id>               Spotify client ID
--spotify-secret <secret>       Spotify client secret
--spotify-token <token>         Spotify access token
--spotify-refresh <token>       Spotify refresh token
--remove-from-source            Remove downloaded tracks from source playlist
```
#### YouTube Options 
```
--youtube-key <key>             Youtube data API key
--get-deleted                   Attempt to retrieve titles of deleted videos from wayback
                                machine. Requires yt-dlp.
--deleted-only                  Only retrieve & download deleted music.
```
#### CSV File Options
```
--artist-col                    Artist column name
--title-col                     Track title column name
--album-col                     Album column name
--length-col                    Track length column name
--album-track-count-col         Album track count column name (sets --album-track-count)
--yt-desc-col                   Youtube description column (improves --yt-parse)
--yt-id-col                     Youtube video id column (improves --yt-parse)

--time-format <format>          Time format in Length column of the csv file (e.g h:m:s.ms
                                for durations like 1:04:35.123). Default: s
--yt-parse                      Enable if the CSV contains YouTube video titles and channel
                                names; attempt to parse them into title and artist names.
--remove-from-source            Remove downloaded tracks from source CSV file
```
#### File Condition Options
```
--format <formats>              Accepted file format(s), comma-separated, without periods
--length-tol <sec>              Length tolerance in seconds
--min-bitrate <rate>            Minimum file bitrate
--max-bitrate <rate>            Maximum file bitrate
--min-samplerate <rate>         Minimum file sample rate
--max-samplerate <rate>         Maximum file sample rate
--min-bitdepth <depth>          Minimum bit depth
--max-bitdepth <depth>          Maximum bit depth
--strict-title                  File name must contain title
--strict-artist                 File path must contain artist name
--strict-album                  File path must contain album name
--banned-users <list>           Comma-separated list of users to ignore

--pref-format <formats>         Preferred file format(s), comma-separated (default: mp3)
--pref-length-tol <sec>         Preferred length tolerance in seconds (default: 3)
--pref-min-bitrate <rate>       Preferred minimum bitrate (default: 200)
--pref-max-bitrate <rate>       Preferred maximum bitrate (default: 2500)
--pref-min-samplerate <rate>    Preferred minimum sample rate
--pref-max-samplerate <rate>    Preferred maximum sample rate (default: 48000)
--pref-min-bitdepth <depth>     Preferred minimum bit depth
--pref-max-bitdepth <depth>     Preferred maximum bit depth
--pref-banned-users <list>      Comma-separated list of users to downrank

--strict-conditions             Skip files with missing properties instead of accepting by
                                default; if --min-bitrate is set, ignores any files with
                                unknown bitrate.
```
#### Album Download Options
```
-a, --album                     Album download mode: Download a folder
-t, --interactive               Interactively select folders. See --help shortcuts.
--album-track-count <num>       Specify the exact number of tracks in the album. Add a + or
                                - for inequalities, e.g '5+' for five or more tracks.
--album-art <option>            Retrieve additional images after downloading the album:
                                'default': No additional images
                                'largest': Download from the folder with the largest image
                                'most': Download from the folder containing the most images
--album-art-only                Only download album art for the provided album
--no-browse-folder              Do not automatically browse user shares to get all files in
                                in the folder
--failed-album-path             Path to move all album files to when one of the items from
                                the directory fails to download. Set to 'delete' to delete
                                the files instead. Set to 'disable' keep them where they 
                                are. Default: {configured output dir}/failed
--album-parallel-search         Run album searches in parallel, then download sequentially.
```
#### Aggregate Download Options
```
-g, --aggregate                 Aggregate download mode: Find and download all distinct
                                songs associated with the provided artist, album, or title.
--aggregate-length-tol <tol>    Max length tolerance in seconds to consider two tracks or
                                albums equal. (Default: 3)
--min-shares-aggregate <num>    Minimum number of shares of a track or album for it to be
                                downloaded in aggregate mode. (Default: 2)
--relax-filtering               Slightly relax file filtering in aggregate mode to include
                                more results
```
### Notes
- Flags can be explicitly disabled by setting them to false, e.g `--interactive false`.
- Single-character flags can be combined, e.g. `-at` for `-a -t`.
- Acronyms of two- and `--three-word-flags` like `--twf` are also accepted. E.g. `--Mbr` for `--max-bitrate`.

## Input types

The input type is usually determined automatically, however it's possible to manually set it
with `--input-type`. The following input types are available:

###  CSV file
Path to a local CSV file. Use a csv file containing track information to download a list of
songs or albums. Only the title or album column is required, but extra info may improve search
result ranking. If the columns have common names ('Artist', 'Title', 'Album', 'Length', etc)
then it's not required to manually specify them. Rows that do not have any text in the title
column will be treated as album downloads.

###  YouTube
A YouTube playlist url. Download songs from a youtube playlist.
The default method to retrieve playlists does not reliably return all videos. To get all
video titles, you can use the official API by providing a key with `--youtube-key`. A key can
be obtained at https://console.cloud.google.com. Create a new project, click 'Enable Api' and
search for 'youtube data', then follow the prompts.

### Spotify
A playlist/album url, or 'spotify-likes'. Download a spotify playlist, album, or your
liked songs. Credentials are required when downloading a private playlist or liked music.

#### Using Credentials

<details>
  <summary>Click to expand</summary>

Create a Spotify application at https://developer.spotify.com/dashboard/applications with a redirect url http://localhost:48721/callback. Obtain an application ID and secret from the created application dashboard.

Start sldl with the obtained credentials and an authorized action to trigger the Spotify app login flow:

```
sldl spotify-likes --spotify-id 123456 --spotify-secret 123456 -n 1 --print-tracks
```
sldl will try to open a browser automatically but will fallback to logging the login flow URL to output. After login flow is complete sldl will output a token and refresh token and finish running the current command.

To skip requiring login flow every time sldl is used the token and refresh token can be provided to sldl (hint: store this info in the config file to make commands less verbose):

```
sldl spotify-likes --spotify-id 123456 --spotify-secret 123456 --spotify-refresh 123456 --spotify-token 123456 -n 1 --pt
```

spotify-token access is only valid for 1 hour. spotify-refresh will enable sldl to renew access every time it is run (and can be used without including spotify-token)
</details>

### Bandcamp
A bandcamp track, album, or artist url. Download a single track, an album, or an artist's
entire discography.

### Soulseek Link
A direct path starting with `slsk://`. Paths ending in `/` will be treated as album downloads.

### Search string
Name of the track, album, or artist to search for. The input can either be an arbitrary
search string (like what you would type in the soulseek search bar), or a comma-separated
list of properties of the form `title=Song Name, artist=Artist Name, length=215`.

The following properties are accepted: title, artist, album, length (in seconds), 
artist-maybe-wrong, album-track-count.

String input accepts a shorthand for track and album downloads: The input `ARTIST - TITLE`
will be parsed as `artist=ARTIST, title=TITLE` when downloading songs, and
`artist=ARTIST, album=TITLE` when run with `--album`.

### List file
List input must be manually activated with `--input-type=list`. The input is a path to a text
file containing lines of the following form:
```ini
# input                         conditions                    pref. conditions
"artist=Artist,album=Album"     "format=mp3; br>128"          "br >= 320"
```
The input can be any of the above input types. The conditions are added on top of the
configured conditions and can be omitted.   
For album downloads, the above example can be written briefly as `a:"Artist - Album"` (note
that `a:` must appear outside the quotes).

## Download modes

### Normal
The default. Downloads a single file for every input entry.

### Album
sldl will search for the album and download an entire folder including non-audio
files. Activated when the input is a link to a spotify or bandcamp album, when the input
string or csv row has no track title, or when `-a/--album` is enabled.

### Aggregate
With `-g/--aggregate`, sldl performs an ordinary search for the input, then attempts to
group the results into distinct songs and download one of each, starting with the one shared
by the most users. Note that `--min-shares-aggregate` is 2 by default, meaning that songs
shared by only one user will be ignored. Aggregate mode can be used to download all songs by 
an artist.  

### Album Aggregate
Activated when both `--album` and `--aggregate` are enabled. sldl will group shares and
download one of each distinct album, starting with the one shared by the most users. Note
that `--min-shares-aggregate` is 2 by default, meaning that albums shared by only one user
will be ignored. Album-aggregate mode can be used to download the most popular (or all) albums
by an artist. It is recommended to pair it with `--interactive`.

## Searching

### Search Query
The search query is determined as follows:

- For album downloads: Search for 'Artist Album Title'.
- For all other download types: If the title field is non-empty, search for 'Artist Title'.
  Otherwise, search for 'Artist Album'

### Soulseek's rate limits
The server may ban users for 30 minutes if too many searches are performed within a short
timespan. sldl has a search limiter which can be adjusted with `--searches-per-time`
and `--searches-renew-time` (when the limit is reached, the status of the downloads will be
'Waiting'). By default it is configured to allow up to 34 searches every 220 seconds.
The default values were determined through experimentation, so they may be incorrect.

## File conditions

Files not satisfying the required conditions will be ignored. Files satisfying pref-conditions
will be preferred: With `--pref-format flac,wav`, sldl will try to download lossless files if
available while still accepting lossy files.

There are no default required conditions. The default preferred conditions are:
```ini
pref-format = mp3
pref-length-tol = 3
pref-min-bitrate = 200
pref-max-bitrate = 2500
pref-max-samplerate = 48000
pref-strict-title = true
pref-strict-album = true
```
sldl will therefore prefer mp3 files with bitrate between 200 and 2500 kbps, and whose length
differs from the supplied length by no more than 3 seconds. Moreover, it will prefer files
whose paths contain the supplied title and album. Changing the last two preferred conditions is
not recommended.  

Note that files satisfying only a subset of the conditions will be preferred over files that don't
satisfy any condition. Run a search with `--print results-full` to reveal the sorting logic.

Conditions can also be supplied as a semicolon-delimited string with `--cond` and `--pref`, e.g
`--cond "br>=320; format=mp3,ogg; sr<96000"`.

### Note on availability of metadata
Some info may be unavailable depending on the client used by the peer. If (e.g) `--min-bitrate`
is set, then sldl will still accept any file with unknown bitrate. To reject all files where one
or more of the checked properties is null (unknown), enable `--strict-conditions`.  
As a consequence, if `--min-bitrate` is also set then any files shared by users with the default
client will be ignored, since the default client does not broadcast the bitrate. Also note that
the default preferred conditions will already affect ranking with this option due to the bitrate
and samplerate checks.

## Name format

Variables enclosed in {} will be replaced by the corresponding file tag value.
Name format supports subdirectories as well as conditional expressions like {tag1|tag2} - If
tag1 is null, use tag2. String literals enclosed in parentheses are ignored in the null check.

### Examples
- `{artist} - {title}`  
    Always name it 'Artist - Title'. Because some files on Soulseek are untagged, the
    following is generally preferred:
- `{artist( - )title|filename}`  
    If artist and title are not null, name it 'Artist - Title', otherwise use the original
    filename.
- `{albumartist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}`  
    Sort files into artist/album folders if all tags are present, otherwise put them in
    the 'missing-tags' folder.   

### Available variables

The following values are read from the downloaded file's tags:
```
artist                         First artist
artists                        Artists, joined with '&'
albumartist                    First album artist
albumartists                   Album artists, joined with '&'
title                          Track title
album                          Album name
year                           Track year
track                          Track number
disc                           Disc number
length                         Track length (in seconds)
```

The following values are taken from the input source (CSV file data, Spotify, etc):
```
sartist                        Source artist
stitle                         Source track title
salbum                         Source album name
slength                        Source track length
uri                            Track URI
snum                           Source item number (1-indexed, including offset)
row/line                       Line number (1-indexed, only for CSV or list input)
```

Other variables:
```
type                           Track type
state                          Track state
failure-reason                 Reason for failure if any
is-audio                       If track is audio (true/false)
artist-maybe-wrong             If artist might be incorrect (true/false)
slsk-filename                  Soulseek filename without extension
slsk-foldername                Soulseek folder name
extractor                      Name of the extractor used
input                          Input string
item-name                      Name of the playlist/source
default-folder                 Default sldl folder name
bindir                         Base application directory
path                           Download file path
path-noext                     Download file path without extension
ext                            File extension
```

## On-Complete Actions

The `--on-complete` parameter allows executing commands after a track or album is downloaded. Multiple actions can be chained using the `+ ` prefix (note the space after +).

**Syntax:** `--on-complete [prefixes:]command`

### Prefixes
- `1:` - Execute only if track downloaded successfully
- `2:` - Execute only if track failed to download
- `a:` - Execute only after album download
- `s:` - Use shell execute
- `h:` - Hide window
- `r:` - Read command output
- `u:` - Use output to update index (implies `r:`)

When using u: prefix, the command output should be `new_state;new_path` to update the track state and path in the index and playlist.

### Variables

The available variables are the same as in name-format, with the following additions:
- `{exitcode}` - Previous command's exit code
- `{stdout}` - Previous command's stdout (requires r:)
- `{stderr}` - Previous command's stderr (requires r:)
- `{first-exitcode}` - First command's exit code
- `{first-stdout}` - First command's stdout (requires r:)
- `{first-stderr}` - First command's stderr (requires r:)

### Examples

Queue downloaded audio files in foobar2000 (Windows):
```ini
on-complete = 1:h: cmd /c if {is-audio}==true start "" "C:\Program Files\foobar2000\foobar2000.exe" /immediate /add "{path}"
```

Convert downloaded audio files to MP3 (Windows, requires ffmpeg):
```ini
# Check if file is audio and not already MP3
on-complete =   1:h:r: cmd /c if {is-audio}==true if /i not {ext}==.mp3 if not exist "{path-noext}.mp3" echo true

# Convert to MP3 if check passed
on-complete = + 1:h:r: cmd /c if {stdout}==true (ffmpeg -i "{path}" -q:a 0 "{path-noext}.mp3" && echo success)

# Delete original and update index if conversion succeeded
on-complete = + 1:h:u: cmd /c if {stdout}==success (del "{path}" & echo "1;{path-noext}.mp3")
```

## Configuration
### Config Location
sldl will look for a file named sldl.conf in the following locations:
```
~/AppData/Roaming/sldl/sldl.conf
~/.config/sldl/sldl.conf
$XDG_CONFIG_HOME/sldl/sldl.conf
```
as well as in the directory of the executable.

### Syntax
Example config file:
```ini
username = your-username
password = your-password
pref-format = flac
fast-search = true
```
Lines starting with hashtags (#) will be ignored. Tildes in paths are expanded as the user
directory. The path variable `{bindir}` stores the directory of the sldl binary.

### Configuration profiles
Profiles are supported:
```ini
[lossless]
pref-format = flac,wav
```
To activate the above profile, run `--profile lossless`. To list all available profiles,
run `--profile help`.  
Profiles can be activated automatically based on a few simple conditions:
```ini
# never automatically cancel album downloads in interactive mode
[no-stale]
profile-cond = interactive && download-mode == "album"
max-stale-time = 9999999

# download to another location for youtube
[youtube]
profile-cond = input-type == "youtube"
path = ~/downloads/sldl-youtube
```
The following operators are supported for use in profile-cond: &&, ||, ==, !=, !{bool}.  
The following variables are available:
```
input-type        ("youtube"|"csv"|"string"|"bandcamp"|"spotify")
download-mode     ("normal"|"aggregate"|"album"|"album-aggregate")
interactive       (bool)
```

## Shortcuts & interactive mode

### Shortcuts
To cancel a running album download, press `C`.

### Interactive mode
Interactive mode for albums can be enabled with `-t`/`--interactive`. It enables users to choose the desired folder or download specific files from it.

Key bindings:
```
Up/p            previous folder
Down/n          next folder
Enter/d         download selected folder
y               download folder and disable interactive mode
r               retrieve all files in the folder
s               skip current item
Esc/q           quit program
h               print this help text

d:1,2,3         download specific files
d:start-end     download a range of files
f:query         filter folders containing files matching query
cd ..           load parent folder
cd subdir       go to subfolder
```

## Examples

Download tracks from a csv file:
```bash
sldl "tracks.csv"
```
<br>

Download a Spotify playlist, or your liked songs:
```bash
sldl "https://open.spotify.com/playlist/id"
sldl "spotify-likes"
```

<br>

Download the albums of a spotify playlist:
```bash
sldl "https://open.spotify.com/playlist/id" -a
```

<br>

Download a youtube playlist, retrieving deleted video names and with yt-dlp fallback:
```bash
sldl "https://youtube.com/playlist/id" --get-deleted --yt-dlp
```

<br>

Interactive album download:
```bash
sldl "Artist - Some Album" -a -t
```

<br>

Download a specific song by name and length, preferring lossless:
```bash
sldl "MC MENTAL @ HIS BEST, length=242" --pref-format "flac,wav"
``` 

<br>

Download all albums by an artist interactively:
```bash
sldl "artist=MC MENTAL" -a -g -t
```

<br>

Print all songs by an artist which are not in your library:
```bash
sldl "artist=MC MENTAL" -g --skip-music-dir "path/to/music" --print results
```

### Advanced example: Automatic wishlist downloader
Create a file named `wishlist.txt`, and add some items as detailed in [Input types: List](#list-file):
```
"Artist - My Favorite Song"            format=flac
a:"Artist - Some Album"                strict-album=true;album-track-count=5
```
Add a profile to your `sldl.conf`:
```ini
[wishlist]
input = ~/sldl/wishlist.txt 
input-type = list
index-path = ~/sldl/wishlist-index.sldl
album-parallel-search = true
```
This will create a global index file `wishlist-index.sldl` which will be scanned every time sldl is run to skip wishlist items that have already been downloaded. If you want to continue searching until a version satisfying the preferred conditions is found, also add `skip-check-pref-cond = true` (note that this requires the files to remain in the same spot after being downloaded).  

Now you can manually run, or set up a cron job / scheduled task to periodically run sldl with the following option:
```bash
sldl --profile wishlist
```

## Notes
- For macOS builds you can use publish.sh to build the app. Download dotnet from https://dotnet.microsoft.com/en-us/download/dotnet/6.0, then run `chmod +x publish.sh && sh publish.sh`. For intel macs, uncomment the x64 and comment the arm64 section in publish.sh. 
- The printed output may appear duplicated, overlap, or not update on some configurations (new windows terminal, git bash). Use another terminal or `--no-progress` in case of issues.

## Tips

### Filtering Irrelevant Results

sldl typically selects the correct files as long as they appear in the search results. By default, it always downloads the best result without additional filtering. However, you can use the following options to filter your downloads:

- `--strict-title`, `--strict-artist`, `--strict-album`  
  Filters out files whose paths do not include the specified title, artist, or album name (ignoring case and using boundary characters). Note that the preferred versions of these options are enabled by default and hence these are generally only needed in special cases, such as when song or album names are very short (one or two characters).

- `--length-tol`  
  For standard downloads, this option sets a tolerance level by which the file’s length can differ from the input length. The default preference (--pref-length-tol) is set to 3 seconds.

- `--album-track-count`  
  When downloading an album, you can specify this option to ensure the album contains a certain number of tracks. For instance, if the input is a Spotify or Bandcamp album, this field is automatically set to `n+` (where n is the number of tracks on the album). This ensures that only albums with at least n tracks are accepted (useful when there are more complete versions of the album on soulseek).  
  Note: The initial search returns only files whose paths match the query, not the full album contents. For example, if you search by a single song name, the search may only show 1 track even if the album contains more. With a lower bound, this folder might be incorrectly excluded; with an upper bound, it might be wrongly accepted. These issues rarely occur when searching with the album name.


### Speeding things up
The following options will make it go faster, but may decrease search result quality or cause
instability:

- `--fast-search` skips waiting until the search completes and downloads as soon as a file matching the preferred conditions is found
- `--concurrent-downloads` can be set it to 4 or more. This only affects normal downloads (not album).
- `--max-stale-time` is set to 30 seconds by default, sldl will wait a long time before giving up on a file.
- `--album-parallel-search` enables parallel searching for album entries

## Docker

A docker container for running `sldl` can be built from this repository. The image supports linux x86/ARM. 

To build and start container:

```shell
clone https://github.com/fiso64/slsk-batchdl
cd slsk-batchdl
docker compose up -d
```

`exec` into the container to start using `sldl`:

```shell
docker compose exec sldl sh
sldl --help
```

The compose stack mounts two directories relative to where `docker-compose.yml` is located which can be used for file management:

* `/config` (at `./config` on host) - put your `sldl.conf` [configuration](#configuration-) in this directory and then use `sldl -c /config ...` to use your configuration in the container
* `/data` (at `./data` on host) - use as the download directory IE `sldl -p /data ...`

### File Permissions

If you are running Docker on a **Linux Host** you should specify `user:group` permissions of the user who owns the **configuration and data directory** on the host to avoid [docker file permission problems.](https://ikriv.com/blog/?p=4698) These can be specified using the [environmental variables **PUID** and **PGID**.](https://docs.linuxserver.io/general/understanding-puid-and-pgid)

To get the UID and GID for the current user run these commands from a terminal:

* `id -u` -- prints UID
* `id -g` -- prints GID

Replace these with the corresponding variable (`PUID` `PGID`) in `docker-compose.yml`.


### Cron

One or more `sldl` commands can be run on a schedule using [cron](https://en.wikipedia.org/wiki/Cron) built into the container.

To create a schedule make a new file on the host `./config/crontabs/abc` and use it with the standard [crontab](https://en.wikipedia.org/wiki/Cron#Overview) syntax.

Make sure to restart the container after any changes to the cron file are made.

Example => Run `sldl` every Sunday at 1am, search for missing tracks from the specified Spotify playlist

```
# min   hour    day     month   weekday command
0 1 * * 0 sldl https://open.spotify.com/playlist/6sf1WR5grXGJ6dET -c /config -p /data --index-path /data/index.sldl
```

[crontab.guru](https://crontab.guru/) could be used to help with the scheduling expression.
