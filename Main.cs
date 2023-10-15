using Gtk;
using System.Text.Json;
using Action = System.Action;
using DateTime = GLib.DateTime;

const string NAME = "salawaat";
const bool DEBUG = true;

var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + '/' + NAME + ".conf";
var tmp = System.IO.Path.GetTempPath() + NAME;

Application application = new("x." + NAME, GLib.ApplicationFlags.None);
application.Register(GLib.Cancellable.Current);
application.Activated += (_, _) => {};

ApplicationWindow window = new(application) {Title = NAME, DefaultSize = new(320, 220)};

Box top = new(Orientation.Vertical, 20);
window.Child = top;

DateTime time = new();

Label date = new("");
Grid table = new() {Halign = Align.Center, RowSpacing = 8, ColumnSpacing = 8};
Expander settingExpander = new("settings");

top.Add(date);
top.Add(table);
top.Add(settingExpander);

Box settingBox = new(Orientation.Vertical, 20) {Halign = Align.Center};
var height = window.AllocatedHeight;

settingExpander.Activated += (_, _) => {
	if (settingBox.Visible = settingExpander.Expanded) {
		settingBox.ShowAll();
		height = window.AllocatedHeight;
	} else {
		window.Resize(window.AllocatedWidth, height);
	}
};

Grid settings = new() {MarginTop = 8, RowSpacing = 8, ColumnSpacing = 8};
Button refresh = new("refresh") {CanDefault = true};
window.Default = refresh;

settingBox.Add(settings);
settingBox.Add(refresh);

var times = new Label[6];
string[] names = {"fajr", "shuruq", "dhuhr", "'asr", "maghrib", "'isha"};

for (var i = 0; i < names.Length; ++i) {
	table.Attach(new Label(names[i]) {Halign = Align.Start}, 0, i, 1, 1);
	table.Attach(times[i] = new("--:--"), 1, i, 1, 1);
}

T settingRow<T>(int row, string name, T input) where T: Widget {
	settings.Attach(new Label(name) {Halign = Align.Start}, 0, row, 1, 1);
	settings.Attach(input, 1, row, 1, 1);
	return input;
}

EntryBuffer entrySettingRow(int row, string name, int maxLength, EntryBuffer buffer) {
	settingRow(row, name, new Entry(buffer) {ActivatesDefault = true, MaxLength = maxLength});
	return buffer;
}

var year = entrySettingRow(0, "year", 7, new(time.Year.ToString(), -1));
var timeZone = entrySettingRow(1, "time zone", 32, new(TimeZoneInfo.Local.Id, -1));
var latitude = entrySettingRow(2, "latitude", 20, new("", -1));
var longitude = entrySettingRow(3, "longitude", 20, new("", -1));
var timeFormat = settingRow(4, "12 hour time", new Switch() {Halign = Align.Start});

window.ShowAll();
top.Add(settingBox);

date.FrameClock.Paint += (_, _) => {
	DateTime now = new();

	if (now.DayOfMonth != time.DayOfMonth && now.Second % 15 == 0) load();
	else if (now.Second != time.Second) setDate(time = now);
};

refresh.Clicked += (_, _) => load();

load(true);

return application.Run(application.ApplicationId, new string[0]);

void setDate(DateTime t) => date.Markup = t.Format("<b>%A %x %X</b>");

void debug(string format, params object[] arguments) {
	if (DEBUG) Console.WriteLine(format, arguments);
}

void warning(string format, params object[] arguments) {
	Console.Error.WriteLine(format, arguments);
}

void load(bool loadConfiguration = false) => Task.Run(() => {
	lock (window) {
		void finish(Action finisher) => GLib.Idle.Add(GLib.Priority.DefaultIdle, () => {
			finisher();
			return false;
		});

		if (loadConfiguration) {
			debug("Loading configuration.");

			if (File.Exists(config)) {
				var contents = File.ReadAllLines(config).Where(line => line.Length > 0).ToArray();

				if (contents.Length >= 1) timeZone.Text = contents[0];
				if (contents.Length >= 2) latitude.Text = contents[1];
				if (contents.Length >= 3) longitude.Text = contents[2];
				if (contents.Length >= 4) timeFormat.Active = contents[3] == "true";
				if (contents.Length != 4) warning("Configuration file is corrupt.");
			}
		}

		if (new[]{year, timeZone, latitude, longitude}.Any(field => field.Text.Length == 0)) {
			finish(() => settingExpander.Expanded = true);
			return;
		}

		DateTime time = new();

		var path = tmp + '/' + string.Join('-', year.Text, timeZone.Text, latitude.Text, longitude.Text, timeFormat.Active).Replace('/', '_');
		debug("path " + path);

		Directory.CreateDirectory(tmp);
		Times[]? days = null;
		MessageDialog? alert = null;

		if (File.Exists(path)) {
			days = JsonSerializer.Deserialize<Times[]>(File.OpenRead(path), new JsonSerializerOptions() {IncludeFields = true});
		}

		if (days == null) {
			var url = $"https://www.moonsighting.com/time_json.php?year={year.Text}&tz={timeZone.Text}&lat={latitude.Text}&lon={longitude.Text}&method=2&both=false&time={timeFormat.Active:d}";
			debug("URL " + url);

			var response = new HttpClient().Send(new() {RequestUri = new(url)});
			debug("HTTP {0:d}", response.StatusCode);

			if (response.IsSuccessStatusCode) {
				var json = JsonSerializer.Deserialize<SourceTimes>(response.Content.ReadAsStream(), new JsonSerializerOptions() {IncludeFields = true});
				days = json.times.Select(day => {
					object times = day.times;

					foreach (var t in times.GetType().GetFields()) {
						var value = ((string) t.GetValue(times)!).TrimEnd();
						if (value.StartsWith('0')) value = value.Substring(1);
						t.SetValue(times, value);
					}

					return (Times) times;
				}).ToArray();

				using (var file = File.OpenWrite(path)) JsonSerializer.Serialize(file, days, new JsonSerializerOptions() {IncludeFields = true});
			} else {
				alert = new(window, 0, MessageType.Error, ButtonsType.Close, false, "Server responded with code {0:d}. Request URL is {1}.", response.StatusCode, url);
			}
		}

		if (days == null) {
			if (alert == null) alert = new(window, 0, MessageType.Error, ButtonsType.Close, false, "An unknown error occurred.");

			finish(() => alert.Present());
			return;
		}

		File.WriteAllText(config, string.Join('\n', timeZone.Text, latitude.Text, longitude.Text, timeFormat.Active));

		var today = days[time.DayOfYear];

		finish(() => {
			string[] keys = {"fajr", "sunrise", "dhuhr", "asr", "maghrib", "isha"};

			for (var i = 0; i < keys.Length; ++i) {
				var time1 = time;
				var t = System.DateTime.Parse((string) today.GetType().GetField(keys[i])!.GetValue(today)!);
				time1 = time1.AddHours(t.Hour - time1.Hour).AddMinutes(t.Minute - time1.Minute);
	
				var output = time1.Format("%R");
				if (output[0] == '0') output = output.Substring(1);
				times[i].Text = output;
			}

			setDate(time);
		});
	}
});

public struct SourceTimes {
	public Query? query;
	public Day[] times;
}

public struct Query {
	public string latitude, longitude, timeZone, method, year, both, time;
}

public struct Day {
	public string day;
	public Times times;
}

public struct Times {
	public string fajr, sunrise, dhuhr, asr, maghrib, isha;
	public string? asr_s, asr_h;
}
