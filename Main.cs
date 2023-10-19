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
setDate(time);
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

window.ShowAll();
top.Add(settingBox);

refresh.Clicked += (_, _) => load();

tick();
load(true);

return application.Run(application.ApplicationId, new string[0]);

void setDate(DateTime t) => date.Markup = t.Format("<b>%A %x %X</b>");

void debug(string format, params object[] arguments) {
	if (DEBUG) Console.WriteLine(format, arguments);
}

void warning(string format, params object[] arguments) {
	Console.Error.WriteLine(format, arguments);
}

void tick() => GLib.Timeout.Add((uint) (1000 - time.Microsecond / 1000), () => {
	DateTime now = new();

	if (now.DayOfMonth != time.DayOfMonth) {
		if (now.Second % 15 == 0) load();
	} else setDate(time = now);

	tick();
	return false;
});

void load(bool loadConfiguration = false) => Task.Run(() => {
	lock (window) {
		Task enbuffer(Action action) {
			var task = new Task(action);

			GLib.Idle.Add(() => {
				task.RunSynchronously();
				return false;
			});

			return task;
		}

		if (loadConfiguration) {
			debug("Loading configuration.");

			if (File.Exists(config)) {
				var contents = File.ReadAllLines(config).Where(line => line.Length > 0).ToArray();

				enbuffer(() => {
					if (contents.Length >= 1) timeZone.Text = contents[0];
					if (contents.Length >= 2) latitude.Text = contents[1];
					if (contents.Length >= 3) longitude.Text = contents[2];
					if (contents.Length != 3) warning("Configuration file is corrupt.");
				}).Wait();
			}
		}

		if (new[]{year, timeZone, latitude, longitude}.Any(field => field.Text.Length == 0)) {
			enbuffer(() => settingExpander.Expanded = true);
			return;
		}

		DateTime requestTime = new();

		var path = tmp + '/' + string.Join('-', year.Text, timeZone.Text, latitude.Text, longitude.Text).Replace('/', '_');
		debug("path " + path);

		Directory.CreateDirectory(tmp);
		Times[]? days = null;
		MessageDialog? alert = null;

		if (File.Exists(path)) {
			days = JsonSerializer.Deserialize<Times[]>(File.OpenRead(path), new JsonSerializerOptions() {IncludeFields = true});
		}

		if (days == null) {
			var url = $"https://www.moonsighting.com/time_json.php?year={year.Text}&tz={timeZone.Text}&lat={latitude.Text}&lon={longitude.Text}&method=2&both=0&time=0";
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

			enbuffer(() => alert.Present());
			return;
		}

		File.WriteAllText(config, string.Join('\n', timeZone.Text, latitude.Text, longitude.Text));

		var today = days[requestTime.DayOfYear];

		enbuffer(() => {
			string[] keys = {"fajr", "sunrise", "dhuhr", "asr", "maghrib", "isha"};

			for (var i = 0; i < keys.Length; ++i) {
				var time1 = requestTime;
				var t = System.DateTime.Parse((string) today.GetType().GetField(keys[i])!.GetValue(today)!);
				time1 = time1.AddHours(t.Hour - time1.Hour).AddMinutes(t.Minute - time1.Minute);
	
				var output = time1.Format("%R");
				if (output[0] == '0') output = output.Substring(1);
				times[i].Text = output;
			}

			if (requestTime.DayOfMonth != time.DayOfMonth) setDate(requestTime);
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
