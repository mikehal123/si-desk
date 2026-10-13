if (args.Length == 1)
{
    Directory.CreateDirectory(args[0]);
    var urls = new[] { "http://127.0.0.1:8788/#" + new string('a', 64), "https://desktop.example-tailnet.ts.net:8788/#" + new string('b', 64) };
    for (var i = 0; i < urls.Length; i++) File.WriteAllBytes(Path.Combine(args[0], "pair-fixture-" + i + ".png"), Sovereign.Desktop.Phone.QrCode.Png(urls[i]));
    File.WriteAllText(Path.Combine(args[0], "expected-qr.json"), System.Text.Json.JsonSerializer.Serialize(urls));
}
var phone = await Sovereign.Desktop.Phone.PhoneAccessChecks.RunAsync();
var grant = await Sovereign.Desktop.Phone.Tests.GrantChecks.RunAsync();
return phone != 0 ? phone : grant;
