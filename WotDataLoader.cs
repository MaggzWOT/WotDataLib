using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Lingo;
using RT.Util.Serialization;

namespace WotDataLib
{
    static class WotDataLoader
    {
        public static WotContext Load(string dataPath, GameInstallation installation, string defaultAuthor, string exportPath)
        {
            if (installation.GameVersionId == null)
                throw new WotDataUserError("No supported game version detected at the specified installation path.");

            var warnings = new List<string>();
            if (!Directory.Exists(dataPath))
                return null; // because there are no game version configs available

            var versionConfigs = LoadGameVersionConfig(dataPath, warnings);
            var versionConfig = versionConfigs.Where(v => v.GameVersionId <= installation.GameVersionId.Value).MaxElementOrDefault(v => v.GameVersionId);
            // versionConfig may be null here

            var wd = new WdData(installation, versionConfig);
            warnings.AddRange(wd.Warnings);

            var clientData = LoadFromClient(wd, installation, warnings);
            if (exportPath != null)
                ExportClientData(clientData, exportPath);
            var builtin = LoadBuiltInFiles(dataPath, warnings, clientData.Item1);
            var extras = LoadDataExtraFiles(dataPath, warnings, clientData.Item2);

            if (!builtin.Any() || !versionConfigs.Any())
                warnings.Add(WdUtil.Tr.Error.DataDir_NoFilesAvailable);

            var result = Resolve(installation, versionConfig, builtin, extras, warnings, defaultAuthor);
            foreach (var tank in result.Tanks)
                tank.ClientData = wd.Tanks.FirstOrDefault(cd => cd.Id == tank.TankId);
            return result;
        }

        private static List<GameVersionConfig> LoadGameVersionConfig(string dataPath, List<string> warnings)
        {
            var result = new List<GameVersionConfig>();
            foreach (var fi in new DirectoryInfo(dataPath).GetFiles("WotGameVersion-*.xml"))
            {
                var parts = fi.Name.Substring(0, fi.Name.Length - 4).Split('-');

                if (parts.Length != 2)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_WrongParts.Fmt(fi.Name, "2", parts.Length));
                    continue;
                }

                if (!parts[1].StartsWith("#") || !int.TryParse(parts[1].Substring(1), out int gameVersionId))
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_GameVersion.Fmt(fi.Name, parts[1]));
                    continue;
                }

                try
                {
                    var ver = ClassifyXml.DeserializeFile<GameVersionConfig>(fi.FullName);
                    ver.GameVersionId = gameVersionId;
                    result.Add(ver);
                }
                catch (Exception e)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_FileError.Fmt(fi.Name, e.Message));
                    continue;
                }
            }
            return result;
        }

        private static Tuple<UnresolvedBuiltIn, List<UnresolvedExtraFileCol>> LoadFromClient(WdData wd, GameInstallation installation, List<string> warnings)
        {
            // Built-in
            var builtin = new UnresolvedBuiltIn
            {
                FileVersion = 0,
                Entries = new List<TankEntry>()
            };
            foreach (var tank in wd.Tanks)
            {
                Country country;
                switch (tank.Country.Name)
                {
                    case "ussr": country = Country.USSR; break;
                    case "germany": country = Country.Germany; break;
                    case "usa": country = Country.USA; break;
                    case "france": country = Country.France; break;
                    case "china": country = Country.China; break;
                    case "uk": country = Country.UK; break;
                    case "japan": country = Country.Japan; break;
                    case "czech": country = Country.Czech; break;
                    case "sweden": country = Country.Sweden; break;
                    case "poland": country = Country.Poland; break;
                    case "italy": country = Country.Italy; break;
                    default:
                        warnings.Add("Unknown country in game data: " + tank.Country.Name);
                        continue;
                }

                Class class_;
                switch (tank.Class)
                {
                    case "lightTank": class_ = Class.Light; break;
                    case "mediumTank": class_ = Class.Medium; break;
                    case "heavyTank": class_ = Class.Heavy; break;
                    case "SPG": class_ = Class.Artillery; break;
                    case "AT-SPG": class_ = Class.Destroyer; break;
                    default:
                        warnings.Add("Unknown tank class in game data; tags: " + tank.Tags.JoinString(", "));
                        continue;
                }

                var kind = tank.Collector ? Category.Collector : tank.Special ? Category.Special : tank.Gold ? Category.Premium : Category.Normal;

                builtin.Entries.Add(
                    new TankEntry(tank.Id, country, tank.Tier, class_, kind, installation.GameVersionId, false)
                );
            }


            // NameFull / Wargaming
            var nameFull = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("NameFull", null, "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Полные названия танков, оригинал – как в игре." },
                { "En", "Full tank names, original – like in the game." },
            },
                Entries = wd.Tanks.Select(tank => new ExtraEntry(tank.Id, tank.FullName, installation.GameVersionId)).ToList()
            };


            // NameShort / Wargaming
            var nameShort = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("NameShort", null, "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Короткие названия танков, оригинал – как в игре." },
                { "En", "Short tank names, original – like in the game." },
            },
                Entries = wd.Tanks.Select(tank => new ExtraEntry(tank.Id, tank.ShortName, installation.GameVersionId)).ToList()
            };


            // Speed: forward
            var speedForward = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Speed", "Forward", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Максимальная скорость вперед." },
                { "En", "Maximum forward speed." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, tank.MaxSpeedForward.ToString(), installation.GameVersionId)
            ).ToList()
            };


            // Speed: reverse
            var speedReverse = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Speed", "Reverse", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Максимальная скорость назад." },
                { "En", "Maximum reverse speed." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, tank.MaxSpeedReverse.ToString(), installation.GameVersionId)
            ).ToList()
            };


            // Armor: hull
            var armorHull = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Armor", "Hull", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Бронирование корпуса (перед, бока, зад)." },
                { "En", "Hull armor thickness (front, sides, rear)." },
            }
            };
            var armorStr = Ut.Lambda((decimal? main, decimal? align) =>
            {
                if (main == null)
                    return "";
                var mainStr = Math.Round(main.Value).ToString();
                var alignStr = align == null ? "" : Math.Round(align.Value).ToString();
                return mainStr.PadLeft(Math.Max(mainStr.Length, alignStr.Length));
            });
            armorHull.Entries = wd.Tanks.Select(tank =>
                new ExtraEntry(tank.Id,
                    armorStr(tank.Hull.ArmorThicknessFront, tank.TopTurret.NullOr(t => t.ArmorThicknessFront)) + " " +
                    armorStr(tank.Hull.ArmorThicknessSide, tank.TopTurret.NullOr(t => t.ArmorThicknessSide)) + " " +
                    armorStr(tank.Hull.ArmorThicknessBack, tank.TopTurret.NullOr(t => t.ArmorThicknessBack)),
                    installation.GameVersionId)
            ).ToList();


            // Armor: top turret
            var armorTurret = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Armor", "Turret", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Бронирование топ башни (перед, бока, зад)." },
                { "En", "Top turret armor thickness (front, sides, rear)." },
            },
                Entries = wd.Tanks.Where(t => t.TopTurret.Rotates).Select(tank =>
                    new ExtraEntry(tank.Id,
                        armorStr(tank.TopTurret.NullOr(t => t.ArmorThicknessFront), tank.Hull.ArmorThicknessFront) + " " +
                        armorStr(tank.TopTurret.NullOr(t => t.ArmorThicknessSide), tank.Hull.ArmorThicknessSide) + " " +
                        armorStr(tank.TopTurret.NullOr(t => t.ArmorThicknessBack), tank.Hull.ArmorThicknessBack),
                        installation.GameVersionId)
            ).ToList()
            };
            armorTurret.Entries.RemoveAll(e => e.Value.Trim().Replace(" ", "") == "000");


            // Visibility: top turret
            var visibility = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Visibility", "TopTurretBase", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Базовый обзор из топ башни." },
                { "En", "Base visibility with top turret." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, Math.Round(tank.TopTurret.VisionDistance).ToString(), installation.GameVersionId)
            ).ToList()
            };


            // Total hit points
            var hitPointsTotal = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("HitPoints", "Tank", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Прочность танка с топ башней." },
                { "En", "Tank hit points with the top turret." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, (tank.Hull.HitPoints + tank.TopTurret.HitPoints).ToString(), installation.GameVersionId)
            ).ToList()
            };


            // Has turret
            var hasTurret = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("HasTurret", null, "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Содержит \"*\" для танков, имеющих вращающуюся башню." },
                { "En", "Contains \"*\" for every tank which has a rotating turret." },
            },
                Entries = wd.Tanks.Where(t => t.TopTurret.Rotates).Select(tank =>
                    new ExtraEntry(tank.Id, "*", installation.GameVersionId)
            ).ToList()
            };


            // Has drum
            var hasDrumAnyGun = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("HasDrum", "AnyGun", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Содержит \"*\" для танков, имеющих доступ к пушке с барабаном." },
                { "En", "Contains \"*\" for tanks which have access to a gun with a drum." },
            },
                Entries = wd.Tanks.Where(t => t.Turrets.Any(r => r.Guns.Any(g => g.HasDrum))).Select(tank =>
                    new ExtraEntry(tank.Id, "*", installation.GameVersionId)
            ).ToList()
            };


            // Has drum
            var hasDrumTopGun = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("HasDrum", "TopGun", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Содержит \"*\" для танков, чья топовая пушка имеет барабан." },
                { "En", "Contains \"*\" for tanks whose top gun has a drum." },
            },
                Entries = wd.Tanks.Where(t => t.TopGun.HasDrum).Select(tank =>
                    new ExtraEntry(tank.Id, "*", installation.GameVersionId)
            ).ToList()
            };


            // Has wheels
            var hasWheels = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("HasWheels", null, "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Содержит \"*\" для колесной техники, пусто для остальной техники." },
                { "En", "Contains \"*\" for wheeled vehicles, empty for the rest of the vehicle" },
            },
                Entries = wd.Tanks.Where(t => t.Chassis.Any(c => c.HasWheels)).Select(tank =>
                    new ExtraEntry(tank.Id, "*", installation.GameVersionId)
            ).ToList()
            };


            // Maximum penetration
            var gunsMaxPenetration = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Guns", "MaxPenetration", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Пробитие лучшей пушкой/снарядом (кроме голды)." },
                { "En", "Penetration using the best gun/shell (except premium shells)." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, (tank.Turrets.SelectMany(t => t.Guns).SelectMany(g => g.Shells).Where(s => !s.Gold).Max(s => s.PenetrationArmor)).ToString(),
                        installation.GameVersionId)
            ).ToList()
            };


            // Maximum damage
            var gunsMaxDamage = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Guns", "MaxDamage", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Урон лучшей пушкой/снарядом (кроме голды)." },
                { "En", "Damage using the best gun/shell (except premium shells)." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, (tank.Turrets.SelectMany(t => t.Guns).SelectMany(g => g.Shells).Where(s => !s.Gold).Max(s => s.DamageArmor)).ToString(),
                        installation.GameVersionId)
            ).ToList()
            };


            // Damage using the most penetrating shell
            var gunsMaxPenetrationDamage = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Guns", "MaxPenetrationDamage", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Урон пушкой/снарядом с лучшим пробитием (кроме голды)." },
                { "En", "Damage using the most penetrating gun/shell (except premium shells)." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, (tank.Turrets.SelectMany(t => t.Guns).SelectMany(g => g.Shells).Where(s => !s.Gold).MaxElement(s => s.PenetrationArmor).DamageArmor).ToString(),
                        installation.GameVersionId)
            ).ToList()
            };


            // Top gun reload time
            var gunsTopGunReloadTime = new UnresolvedExtraFileCol
            {
                PropertyId = new ExtraPropertyId("Guns", "TopGunReloadTime", "Wargaming"),
                FileVersion = 0,
                Descriptions = new Dictionary<string, string>
            {
                { "Ru", "Время перезарядки топового орудия." },
                { "En", "Top gun reload time." },
            },
                Entries = wd.Tanks.Select(tank =>
                    new ExtraEntry(tank.Id, tank.TopGun.ReloadTime.ToString(),
                        installation.GameVersionId)
            ).ToList()
            };


            return Tuple.Create(builtin, new List<UnresolvedExtraFileCol> { nameFull, nameShort, speedForward, speedReverse, armorHull, armorTurret,
                visibility, hitPointsTotal, hasTurret, hasDrumAnyGun, hasDrumTopGun, hasWheels, gunsMaxPenetration, gunsMaxDamage, gunsMaxPenetrationDamage, gunsTopGunReloadTime });
        }

        private static string CsvEscape(string str)
        {
            if (str.Contains(',') || str.Contains('"'))
                return "\"" + str.Replace("\"", "\"\"") + "\"";
            else
                return str;
        }

        private static void ExportClientData(Tuple<UnresolvedBuiltIn, List<UnresolvedExtraFileCol>> data, string path)
        {
            try { Directory.CreateDirectory(path); }
            catch { }

            // Delete all the existing export files
            foreach (var f in Directory.GetFiles(path, "Exported-WotBuiltIn-*.csv").Concat(Directory.GetFiles(path, "Exported-WotData-*.csv")))
                try { File.Delete(Path.Combine(path, f)); }
                catch { }

            // Export built-in
            try
            {
                using (var file = File.Open(Path.Combine(path, "Exported-WotBuiltIn-0.csv"), FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(file))
                {
                    sw.WriteLine("WOT-BUILTIN,2");
                    foreach (var entry in data.Item1.Entries.OrderBy(e => e.TankId))
                    {
                        sw.Write(entry.TankId);
                        sw.Write(',');
                        if (entry.Country != null)
                            sw.Write(entry.Country.Value.ToString().ToLowerInvariant());
                        sw.Write(',');
                        if (entry.Tier != null)
                            sw.Write(entry.Tier.Value);
                        sw.Write(',');
                        if (entry.Class != null)
                            sw.Write(entry.Class.Value.ToString().ToLowerInvariant());
                        sw.Write(',');
                        if (entry.Category != null)
                            sw.Write(entry.Category.Value.ToString().ToLowerInvariant());
                        if (entry.GameVersionId != null)
                            sw.Write(",#" + entry.GameVersionId.Value);
                        sw.WriteLine();
                    }
                }
            }
            catch { }

            // Export extra properties
            foreach (var properties in data.Item2.GroupBy(p => p.PropertyId.FileId + "-" + p.PropertyId.Author))
            {
                try
                {
                    var props = properties.OrderBy(p => p.PropertyId.ColumnId ?? "").ToList();
                    var filename = Path.Combine(path, "Exported-WotData-{0}-0.csv".Fmt(properties.Key));
                    using (var file = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var sw = new StreamWriter(file))
                    {
                        sw.WriteLine("WOT-DATA,2");
                        if (props.Any(p => p.PropertyId.ColumnId != null))
                            sw.WriteLine("{{ID}}," + props.Select(p => CsvEscape(p.PropertyId.ColumnId)).JoinString(","));
                        foreach (var descLang in props.SelectMany(p => p.Descriptions.Keys).Distinct().Order())
                            sw.WriteLine("{{" + descLang.ToUpper() + "}}," + props.Select(p => p.Descriptions.ContainsKey(descLang) ? CsvEscape(p.Descriptions[descLang]) : "").JoinString(","));
                        if (props.Any(p => p.InheritsFrom != null))
                            sw.WriteLine("{{Inherit}}," + props.Select(p => CsvEscape(p.InheritsFrom)).JoinString(","));
                        foreach (var tankId in props.SelectMany(p => p.Entries).Select(e => e.TankId).Distinct().Order())
                        {
                            sw.Write(tankId);
                            int? gameVersionId = null;
                            foreach (var prop in props)
                            {
                                sw.Write(',');
                                var entry = prop.Entries.Where(e => e.TankId == tankId).FirstOrDefault();
                                if (entry != null)
                                {
                                    sw.Write(CsvEscape(entry.Value));
                                    gameVersionId = gameVersionId ?? entry.GameVersionId;
                                }
                            }
                            if (gameVersionId != null)
                                sw.Write(",#" + gameVersionId.Value);
                            sw.WriteLine();
                        }
                    }
                }
                catch { }
            }
        }

        private static Dictionary<string, List<TankEntry>> LoadBuiltInFiles(string dataPath, List<string> warnings, UnresolvedBuiltIn fromClient)
        {
            var builtin = new List<UnresolvedBuiltIn>
            {
                fromClient
            };
            var origFilenames = new Dictionary<object, string>();
            foreach (var fi in new DirectoryInfo(dataPath).GetFiles("WotBuiltIn-*.csv"))
            {
                var parts = fi.Name.Substring(0, fi.Name.Length - 4).Split('-');

                if (parts.Length != 2)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_WrongParts.Fmt(fi.Name, "2", parts.Length));
                    continue;
                }

                if (!int.TryParse(parts[1], out int fileVersion) || fileVersion < 1)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_FileVersion.Fmt(fi.Name, parts[1]));
                    continue;
                }

                try
                {
                    var df = LoadBuiltInFile(fileVersion, fi.FullName);
                    builtin.Add(df);
                    origFilenames[df] = fi.Name;
                }
                catch (Exception e)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_FileError.Fmt(fi.Name, e.Message));
                    continue;
                }
            }

            return ResolveBuiltIns(warnings, builtin, origFilenames);
        }

        private static List<ResolvedExtraProperty> LoadDataExtraFiles(string dataPath, List<string> warnings, List<UnresolvedExtraFileCol> clientData)
        {
            var extra = new List<UnresolvedExtraFileCol>();
            extra.AddRange(clientData);
            var origFilenames = new Dictionary<object, string>();
            foreach (var fi in new DirectoryInfo(dataPath).GetFiles("WotData-*.csv"))
            {
                var parts = fi.Name.Substring(0, fi.Name.Length - 4).Split('-');

                if (parts.Length != 4)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_WrongParts.Fmt(fi.Name, "4", parts.Length));
                    continue;
                }

                if (!int.TryParse(parts[3], out int fileVersion) || fileVersion < 1)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_FileVersion.Fmt(fi.Name, parts[3]));
                    continue;
                }

                string name = parts[1].Trim();
                if (name.Length == 0)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_PropName.Fmt(fi.Name));
                    continue;
                }

                string author = parts[2].Trim();
                if (author.Length == 0)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_Author.Fmt(fi.Name));
                    continue;
                }

                try
                {
                    var df = LoadExtraFile(name, author, fileVersion, fi.FullName);
                    foreach (var prop in df)
                    {
                        extra.Add(prop);
                        origFilenames[prop] = fi.Name;
                    }
                }
                catch (Exception e)
                {
                    warnings.Add(WdUtil.Tr.Error.DataDir_Skip_FileError.Fmt(fi.Name, e.Message));
                    continue;
                }
            }

            return ResolveExtras(warnings, extra, origFilenames);
        }

        private static readonly HashSet<string> _languages = EnumStrong.GetValues<Language>().Select(l => l.GetIsoLanguageCode()).Concat("en").ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static UnresolvedBuiltIn LoadBuiltInFile(int fileVersion, string filename)
        {
            var result = new UnresolvedBuiltIn
            {
                FileVersion = fileVersion
            };

            var lines = WdUtil.ReadCsvLines(filename).ToArray();
            if (lines.Length == 0)
                throw new WotDataUserError(WdUtil.Tr.Error.DataFile_EmptyFile);
            var header = lines[0].Item2;
            if (header.Length < 2)
                throw new WotDataUserError(WdUtil.Tr.Error.DataFile_TooFewFieldsFirstLine);
            if (header[0] != "WOT-BUILTIN")
                throw new WotDataUserError(WdUtil.Tr.Error.DataFile_ExpectedSignature.Fmt("WOT-BUILTIN"));
            if (header[1] != "2")
                throw new WotDataUserError(WdUtil.Tr.Error.DataFile_ExpectedV2);

            result.Entries = lines.Skip(1).Select(lp =>
            {
                try
                {
                    var fields = lp.Item2;
                    if (fields.Length < 1)
                        throw new WotDataUserError(WdUtil.Tr.Error.DataFile_TooFewFields.Fmt(WdUtil.Tr, 1));

                    string tankId = fields[0];
                    int? tier = null;
                    Country? country = null;
                    Class? class_ = null;
                    Category? category = null;
                    int? gameVersionId = null;
                    bool delete = false;
                    string imageName = fields[0];

                    if (fields.Length > 1 && fields[1] != "")
                        switch (fields[1])
                        {
                            case "ussr": country = Country.USSR; break;
                            case "germany": country = Country.Germany; break;
                            case "usa": country = Country.USA; break;
                            case "china": country = Country.China; break;
                            case "france": country = Country.France; break;
                            case "uk": country = Country.UK; break;
                            case "japan": country = Country.Japan; break;
                            case "czech": country = Country.Czech; break;
                            case "sweden": country = Country.Sweden; break;
                            case "poland": country = Country.Poland; break;
                            case "italy": country = Country.Italy; break;
                            case "none": country = Country.None; break;
                            default: throw new WotDataUserError(WdUtil.Tr.Error.DataFile_UnrecognizedCountry.Fmt(fields[1],
                                new[] { "ussr", "germany", "usa", "china", "france", "uk", "japan", "czech", "sweden", "poland", "italy", "none" }.JoinString(", ", "\"", "\"")));
                        }

                    if (fields.Length > 2 && fields[2] != "")
                    {
                        if (!int.TryParse(fields[2], out int tierNN))
                            throw new WotDataUserError(string.Format(WdUtil.Tr.Error.DataFile_TankTierValue, fields[2]));
                        if (tierNN < 0 || tierNN > 10)
                            throw new WotDataUserError(string.Format(WdUtil.Tr.Error.DataFile_TankTierValue, fields[2]));
                        tier = tierNN;
                    }

                    if (fields.Length > 3 && fields[3] != "")
                        switch (fields[3])
                        {
                            case "light": class_ = Class.Light; break;
                            case "medium": class_ = Class.Medium; break;
                            case "heavy": class_ = Class.Heavy; break;
                            case "destroyer": class_ = Class.Destroyer; break;
                            case "artillery": class_ = Class.Artillery; break;
                            case "none": class_ = Class.None; break;
                            default: throw new WotDataUserError(WdUtil.Tr.Error.DataFile_UnrecognizedClass.Fmt(fields[3],
                                new[] { "light", "medium", "heavy", "destroyer", "artillery", "none" }.JoinString(", ", "\"", "\"")));
                        }

                    if (fields.Length > 4 && fields[4] != "")
                        switch (fields[4])
                        {
                            case "normal": category = Category.Normal; break;
                            case "premium": category = Category.Premium; break;
                            case "special": category = Category.Special; break;
                            case "collector": category = Category.Collector; break;
                            default: throw new WotDataUserError(WdUtil.Tr.Error.DataFile_UnrecognizedCategory.Fmt(fields[4],
                                new[] { "normal", "premium", "special", "collector" }.JoinString(", ", "\"", "\"")));
                        }

                    if (fields.Length > 5 && fields[5] != "")
                    {
                        if (!fields[5].StartsWith('#') || !int.TryParse(fields[5].Substring(1), out int gameVerId))
                            throw new WotDataUserError("The game version doesn't start with a # or does not parse as a number.");
                        gameVersionId = gameVerId;
                    }

                    if (fields.Length > 6)
                    {
                        if (fields[6] == "del")
                            delete = true;
                        else if (fields[6] != "")
                            throw new WotDataUserError("The very last column must contain the text \"del\" or nothing.");
                    }

                    if (fields.Length > 7 && fields[7] != "")
                    {
                        imageName = fields[7];
                    }

                    return new TankEntry(tankId, country, tier, class_, category, gameVersionId, delete, imageName);
                }
                catch (Exception e)
                {
                    throw new WotDataUserError(WdUtil.Tr.Error.DataFile_LineNum.Fmt(lp.Item1, e.Message));
                }
            }).ToList();

            return result;
        }

        private static List<UnresolvedExtraFileCol> LoadExtraFile(string name, string author, int fileVersion, string filename)
        {
            var lines = WdUtil.ReadCsvLines(filename).ToArray();
            var header = lines[0].Item2;
            if (header.Length < 2)
                throw new WotDataUserError(WdUtil.Tr.Error.DataFile_TooFewFieldsFirstLine);
            if (header[0] != "WOT-DATA")
                throw new WotDataUserError(WdUtil.Tr.Error.DataFile_ExpectedSignature.Fmt("WOT-DATA"));
            if (header[1] != "2")
                throw new WotDataUserError(WdUtil.Tr.Error.DataFile_ExpectedV2);

            bool headersDone = false;
            string[] headerInherit = null;
            string[] headerID = null;
            var headerDesc = new Dictionary<string, string[]>();
            var entries = new AutoDictionary<string, List<ExtraEntry>>(_ => new List<ExtraEntry>());
            for (int l = 1; l < lines.Length; l++)
            {
                try
                {
                    var fields = lines[l].Item2;
                    bool isHeader = fields[0].StartsWith("{{");
                    if (isHeader && headersDone)
                        throw new WotDataUserError("Headers must not be mixed with the rest of the data.");
                    if (isHeader)
                    {
                        if (fields[0].EqualsNoCase("{{ID}}"))
                        {
                            if (headerID != null)
                                throw new WotDataUserError("There must be at most one ID header in the file");
                            headerID = fields.Skip(1).ToArray();
                            if (headerID.Any(f => f == ""))
                                throw new WotDataUserError("ID header fields must not be empty");
                            if (headerID.Length == 0)
                                throw new WotDataUserError("If present, the ID header must have at least one value");
                            var ids = headerID.ToHashSet(StringComparer.OrdinalIgnoreCase);
                            if (ids.Count != headerID.Length)
                                throw new WotDataUserError("Duplicate column IDs are not allowed");
                        }
                        else if (fields[0].EqualsNoCase("{{Inherit}}"))
                        {
                            if (headerInherit != null)
                                throw new WotDataUserError("There must be at most one Inherit header in the file");
                            headerInherit = fields.Skip(1).ToArray();
                        }
                        else
                        {
                            if (!_languages.Contains(fields[0].SubstringSafe(2, fields[0].Length - 4)))
                                throw new WotDataUserError("Unrecognized header: " + fields[0] + " (must be {{ID}}, {{Inherit}} or a language code such as {{En}} or {{Ru}} etc.)");
                            headerDesc.Add(fields[0].Replace("{", "").Replace("}", ""), fields.Skip(1).ToArray());
                        }
                    }
                    else
                    {
                        if (!headersDone)
                        {
                            headersDone = true;
                            // interpret the headers now
                            if (headerID == null)
                                headerID = new string[] { "" }; // defines how many data columns (=properties) we expect
                            if (headerInherit != null && headerInherit.Length > headerID.Length)
                                throw new WotDataUserError("The inherit header contains too many columns");
                            if (headerDesc.Values.Any(v => v.Length > headerID.Length))
                                throw new WotDataUserError("One of the description headers contains too many columns");
                        }

                        if (fields.Length > 1 + headerID.Length + 1)
                            throw new WotDataUserError("One of the data rows contains too many columns (for multiple columns, add an {{ID}} header)");
                        int? version = null;
                        if (fields.Length > 1 + headerID.Length)
                        {
                            if (!fields[1 + headerID.Length].StartsWith("#") || !int.TryParse(fields[1 + headerID.Length].SubstringSafe(1), out int ver))
                                throw new WotDataUserError("The game version field must be a number preceded by a #, e.g. #123");
                            version = ver;
                        }
                        for (int i = 0; i < headerID.Length; i++)
                            if (fields.Length > i + 1 && fields[i + 1] != "")
                                entries[headerID[i]].Add(new ExtraEntry(fields[0], fields[i + 1], version));
                    }
                }
                catch (Exception e)
                {
                    throw new WotDataUserError(WdUtil.Tr.Error.DataFile_LineNum.Fmt(lines[l].Item1, e.Message));
                }
            }

            var properties = new List<UnresolvedExtraFileCol>();
            for (int i = 0; i < headerID.Length; i++)
            {
                var prop = new UnresolvedExtraFileCol
                {
                    PropertyId = new ExtraPropertyId(fileId: name, columnId: headerID[i] == "" ? null : headerID[i], author: author),
                    Descriptions = headerDesc
                    .Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value[i] == "" ? null : kvp.Value[i]))
                    .Where(kvp => kvp.Value != null)
                    .ToDictionary(StringComparer.OrdinalIgnoreCase),
                    Entries = entries[headerID[i]],
                    FileVersion = fileVersion,
                    InheritsFrom = (headerInherit == null || headerInherit.Length <= i || headerInherit[i] == "") ? null : headerInherit[i]
                };
                properties.Add(prop);
            }

            return properties;
        }

        private static Dictionary<string, List<TankEntry>> ResolveBuiltIns(List<string> warnings, List<UnresolvedBuiltIn> builtins, Dictionary<object, string> origFilenames)
        {
            // The following rules apply *per tank*.
            // Within a single file, versioned entries take precedence over unversioned entries. The unversioned entries apply only to
            // game versions before the earliest versioned entry. Only a single unversioned entry is allowed, be it add or delete; otherwise
            // only the last one in the file order is kept (+warning). Also, only a single entry is allowed for each game version, otherwise only
            // the last one is kept (+warning). Redundant entries are also removed (+warning), namely delete entries following another delete
            // (in game version order).
            // With multiple files, an unversioned entry overrides *everything* inherited from the earlier versions, while a versioned entry
            // overrides *everything* from that version onwards.
            var resolved = new AutoDictionary<string, List<TankEntry>>(_ => new List<TankEntry>());
            foreach (var builtin in builtins.OrderBy(b => b.FileVersion))
            {
                // Resolve any instances of the file overriding its own entries, issuing warnings along the way
                foreach (var grp in builtin.Entries.GroupBy(e => e.TankId))
                {
                    // There must be at most one entry per version, and at most one unversioned entry.
                    foreach (var ver in grp.GroupBy(e => e.GameVersionId))
                    {
                        if (grp.Count() > 1)
                        {
                            warnings.Add("WotBuiltIn-{0}: Multiple entries found for tank {1} and game version {2}; all except for the last one will be ignored.".Fmt(builtin.FileVersion, grp.Key, ver.Key == null ? "<unspecified>" : ver.Key.ToString()));
                            var set = ver.SkipLast(1).ToHashSet();
                            builtin.Entries.RemoveAll(e => set.Contains(e));
                        }
                    }
                    // There must not be any redundant "delete" entries
                    foreach (var conseq in grp.OrderBy(e => e.GameVersionId ?? -1).GroupConsecutiveBy(e => e.Delete).Where(c => c.Key && c.Count > 1).ToList())
                    {
                        warnings.Add("WotBuiltIn-{0}: Redundant \"del\" entries for tank {1} found; ignored.".Fmt(builtin.FileVersion, grp.Key));
                        var set = conseq.Skip(1).ToHashSet();
                        builtin.Entries.RemoveAll(e => set.Contains(e));
                    }
                }

                // Accumulate the overrides. This contains a bit of a screw-up, in that a single row might omit some of the properties. See comments inside the loop.
                // As a result of the screw-up, this method doesn't really fully resolve the data and it still needs further resolving...
                foreach (var cur in builtin.Entries.OrderBy(b => b.GameVersionId == null ? 0 : 1).ThenBy(b => b.Delete ? 0 : 1).ThenBy(b => b.GameVersionId ?? 0))
                {
                    // Note that due to the OrderBy clause, each of the "if"s below cannot encounter any of the entry types listed below itself belonging to the same file version
                    if (cur.GameVersionId == null && cur.Delete)
                    {
                        resolved[cur.TankId].Clear();
                    }
                    else if (cur.GameVersionId == null && !cur.Delete) // spell out for clarity
                    {
                        // resolved[cur.TankId].Clear(); can't do this because the new entry might have some of the properties set to null, so must keep all the earlier entries and resolve later
                        resolved[cur.TankId].Add(cur);
                    }
                    else if (cur.GameVersionId != null && cur.Delete)
                    {
                        resolved[cur.TankId].RemoveAll(e => e.GameVersionId >= cur.GameVersionId);
                    }
                    else if (cur.GameVersionId != null && !cur.Delete) // no other options left but for clarity...
                    {
                        // resolved[cur.TankId].RemoveWhere(e => e.GameVersionId >= cur.GameVersionId); can't do this for the same reason as above
                        resolved[cur.TankId].Add(cur);
                    }
                }
            }

            resolved.RemoveAllByValue(v => v.Count == 0);
            return resolved.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        private static List<ResolvedExtraProperty> ResolveExtras(List<string> warnings, List<UnresolvedExtraFileCol> extraFileCols, Dictionary<object, string> origFilenames)
        {
            // Extra properties are first resolved the same way built-in properties are. Then inheritance is applied, using the
            // "inherit from" value specified in the highest file version (i.e. later files may override the inheritance source).

            var properties = new Dictionary<string, UnresolvedExtraProperty>(StringComparer.OrdinalIgnoreCase);

            foreach (var extraFileColGroup in extraFileCols.GroupBy(ex => ex.PropertyId))
            {
                var property = new UnresolvedExtraProperty
                {
                    PropertyId = extraFileColGroup.First().PropertyId, // there's got to be at least one, otherwise the group couldn't possibly exist
                    Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Entries = new AutoDictionary<string, HashSet<ExtraEntry>>(_ => new HashSet<ExtraEntry>())
                };
                properties.Add(property.PropertyId.ToString(), property);

                foreach (var extraFileCol in extraFileColGroup.OrderBy(e => e.FileVersion))
                {
                    // Resolve any instances of the file overriding its own entries, issuing warnings along the way
                    foreach (var grp in extraFileCol.Entries.GroupBy(e => e.TankId))
                    {
                        // There must be at most one entry per version, and at most one unversioned entry.
                        foreach (var ver in grp.GroupBy(e => e.GameVersionId))
                        {
                            if (ver.Count() > 1)
                            {
                                warnings.Add("WotData-{0}-{1} / {2}: Multiple entries found for tank {3} and game version {4}; all except for the last one will be ignored.".Fmt(extraFileCol.PropertyId.FileId, extraFileCol.FileVersion, extraFileCol.PropertyId.ColumnId, extraFileCol.FileVersion, grp.Key, ver.Key == null ? "<unspecified>" : ver.Key.ToString()));
                                foreach (var entry in ver.SkipLast(1))
                                    extraFileCol.Entries.Remove(entry);
                            }
                        }
                    }

                    // Apply the file's overrides to the global result
                    foreach (var cur in extraFileCol.Entries.OrderBy(b => b.GameVersionId ?? -1))
                    {
                        // Note that due to the OrderBy clause, each of the "if"s below cannot encounter any of the entry types listed below itself belonging to the same file version
                        if (cur.GameVersionId == null)
                        {
                            property.Entries[cur.TankId].Clear();
                            property.Entries[cur.TankId].Add(cur);
                        }
                        else if (cur.GameVersionId != null) // spell out for clarity
                        {
                            property.Entries[cur.TankId].RemoveWhere(e => e.GameVersionId >= cur.GameVersionId);
                            property.Entries[cur.TankId].Add(cur);
                        }
                    }

                    // Override inherits from
                    if (extraFileCol.InheritsFrom != null)
                        property.InheritsFrom = extraFileCol.InheritsFrom == "del" ? null : extraFileCol.InheritsFrom;
                    // Override descriptions
                    foreach (var kvp in extraFileCol.Descriptions)
                        property.Descriptions[kvp.Key] = kvp.Value;
                }
            }

            // Now resolve inheritance

            // First, resolve the immedaite parent of each property that inherits from another one, warn about missing properties
            // and circular references, and remove all properties that cannot be resolved.
            while (true)
            {
                // Remove properties which inherit from a non-existent property
                var ignore = new HashSet<UnresolvedExtraProperty>();
                do
                {
                    ignore.Clear();
                    foreach (var p in properties.Values.Where(p => p.InheritsFrom != null))
                    {
                        if (!properties.ContainsKey(p.InheritsFrom))
                        {
                            warnings.Add(WdUtil.Tr.Error.DataDir_Skip_InhNoProp.Fmt("<?>"/*origFilenames[p] broken... pls fix me*/, p.InheritsFrom));
                            ignore.Add(p);
                            continue;
                        }
                    }
                    properties.RemoveAllByValue(f => ignore.Contains(f));
                } while (ignore.Count > 0);

                // Build the transitive closure of parent-child relationships
                foreach (var p in properties.Values)
                    p.TransitiveChildren.Clear();
                foreach (var p in properties.Values.Where(p => p.InheritsFrom != null))
                {
                    p.ImmediateParent = properties[p.InheritsFrom];
                    p.ImmediateParent.TransitiveChildren.Add(p);
                }
                // Keep adding children's children until no further changes (quite a brute-force algorithm... potential bottleneck)
                bool added;
                do
                {
                    added = false;
                    foreach (var p in properties.Values)
                        foreach (var c1 in p.TransitiveChildren.ToArray())
                            foreach (var c2 in c1.TransitiveChildren)
                                if (!p.TransitiveChildren.Contains(c2))
                                {
                                    p.TransitiveChildren.Add(c2);
                                    added = true;
                                }
                } while (added);

                // If there are no self-referencing properties then we're all done
                var looped = properties.Values.Where(p => p.TransitiveChildren.Contains(p)).ToArray();
                if (looped.Length == 0)
                    break;
                // Otherwise remove one circularly-referencing property and try again.
                warnings.Add(WdUtil.Tr.Error.DataDir_Skip_InhCircular.Fmt("<?>" /*origFilenames[looped[0]] broken... pls fix me*/));
                properties.Remove(looped[0].PropertyId.ToString());
            }

            // Compute the distance to nearest "root" in order to ensure that by the time an immediate parent is inherited from, the parent's inheritance has already been resolved
            foreach (var p in properties.Values)
                p.Depth = p.ImmediateParent == null ? 0 : -1;
            while (properties.Values.Any(p => p.Depth == -1))
            {
                foreach (var p in properties.Values.Where(p => p.Depth == -1 && p.ImmediateParent.Depth != -1))
                    p.Depth = p.ImmediateParent.Depth + 1;
            }

            // Finally, perform the actual inheritance
            foreach (var p in properties.Values.Where(p => p.ImmediateParent != null).OrderBy(df => df.Depth))
            {
                foreach (var kvp in p.ImmediateParent.Entries)
                {
                    var parent = kvp.Value;
                    var child = p.Entries[kvp.Key];
                    // If the child has an unversioned entry, do not inherit any of the parent entries
                    if (child.Any(e => e.GameVersionId == null))
                        continue;
                    // Otherwise mesh the two together, with the child taking precedence only where the same game version ID is specified in both
                    foreach (var pe in parent)
                        if (!child.Any(ce => ce.GameVersionId == pe.GameVersionId))
                            child.Add(pe);
                }
            }

            // Store the results in a publicly accessible, read-only format
            var result = new List<ResolvedExtraProperty>();
            foreach (var p in properties.Values)
            {
                result.Add(new ResolvedExtraProperty(p.PropertyId, p.Descriptions, p.Entries.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.OrderBy(e => e.GameVersionId ?? -1).ToList()
                )));
            }
            return result;
        }

        private static WotContext Resolve(GameInstallation installation, GameVersionConfig versionConfig, Dictionary<string, List<TankEntry>> builtins, List<ResolvedExtraProperty> extras, List<string> warnings, string defaultAuthor)
        {
            var gameVerId = installation.GameVersionId.Value; // cannot be null; the caller checks this
            var context = new WotContext(installation, versionConfig, warnings, defaultAuthor);
            var extrasResolved = new HashSet<ExtraPropertyId>();

            foreach (var builtin in builtins)
            {
                var applicable = builtin.Value.Where(e => e.GameVersionId == null || e.GameVersionId <= gameVerId).ToList();
                if (applicable.Count == 0)
                    continue; // this tank does not exist in the selected game version

                // Resolve the builtin data
                var country = Ut.OnExceptionDefault(() => applicable.Where(e => e.Country != null).Last().Country, null);
                var tier = Ut.OnExceptionDefault(() => applicable.Where(e => e.Tier != null).Last().Tier, null);
                var class_ = Ut.OnExceptionDefault(() => applicable.Where(e => e.Class != null).Last().Class, null);
                var category = Ut.OnExceptionDefault(() => applicable.Where(e => e.Category != null).Last().Category, null);
                var imageName = Ut.OnExceptionDefault(() => applicable.Where(e => e.ImageName != null).Last().ImageName, null);
                if (country == null || tier == null || class_ == null || category == null)
                {
                    warnings.Add("Built-in data for tank {0} is unresolvable: one of the basic properties (country, tier, class or availability) is completely missing after all the inheritance has been resolved.".Fmt(builtin.Key));
                    continue; // this tank is unresolvable for the specified game version
                }

                // Resolve the extra data
                var props = new Dictionary<ExtraPropertyId, string>();
                foreach (var extra in extras)
                {
                    if (!extra.Entries.ContainsKey(builtin.Key))
                        continue;
                    var applicable2 = extra.Entries[builtin.Key].Where(e => e.GameVersionId == null || e.GameVersionId <= gameVerId);
                    if (!applicable2.Any())
                        continue;
                    props.Add(
                        extra.PropertyId,
                        applicable2.Last().Value
                    );
                    extrasResolved.Add(extra.PropertyId);
                }
                context.Tanks.Add(new WotTank(builtin.Key, country.Value, tier.Value, class_.Value, category.Value, imageName, props, context));
            }

            foreach (var prop in extrasResolved)
                context.ExtraProperties.Add(new ExtraPropertyInfo(prop, extras.First(extra => extra.PropertyId == prop).Descriptions));

            context.Freeze();
            return context;
        }

        #region Private classes

        private sealed class TankEntry
        {
            public string TankId { get; private set; }
            public Country? Country { get; private set; }
            public int? Tier { get; private set; }
            public Class? Class { get; private set; }
            public Category? Category { get; private set; }

            public int? GameVersionId { get; private set; }
            public bool Delete { get; private set; }
            public string ImageName { get; private set; }

            public TankEntry(string tankId, Country? country, int? tier, Class? class_, Category? category, int? gameVersionId, bool delete, string imageName = null)
            {
                TankId = tankId;
                Country = country;
                Tier = tier;
                Class = class_;
                Category = category;
                GameVersionId = gameVersionId;
                Delete = delete;
                ImageName = imageName ?? tankId;
            }

            public override string ToString()
            {
                return "Tank: " + TankId + (GameVersionId == null ? "" : " #{0}".Fmt(GameVersionId)) + (Delete ? " del" : "");
            }
        }

        /// <summary>Represents a single value of an "extra" property.</summary>
        private sealed class ExtraEntry
        {
            public string TankId { get; private set; }
            public string Value { get; private set; }
            public int? GameVersionId { get; private set; }

            public ExtraEntry(string tankId, string propertyValue, int? gameVersionId)
            {
                TankId = tankId;
                Value = propertyValue;
                GameVersionId = gameVersionId;
            }

            public override string ToString()
            {
                return "{0} = {1}".Fmt(TankId, Value) + (GameVersionId == null ? "" : " (#{0})".Fmt(GameVersionId));
            }
        }

        private sealed class UnresolvedBuiltIn
        {
            public int FileVersion;
            public List<TankEntry> Entries;
            public override string ToString() { return "WotData-BuiltIn-{0}".Fmt(FileVersion); }
        }

        private sealed class UnresolvedExtraFileCol
        {
            public ExtraPropertyId PropertyId;
            /// <summary>Optional; empty if it wasn't specified.</summary>
            public Dictionary<string, string> Descriptions;
            /// <summary>
            ///     The file version of the file this property comes from. Data read from the game itself has this set to 0.</summary>
            public int FileVersion;
            /// <summary>Optional; null if it wasn't specified.</summary>
            public string InheritsFrom;

            public List<ExtraEntry> Entries;

            public override string ToString()
            {
                return PropertyId.ToString() + " (v{0})".Fmt(FileVersion) + (InheritsFrom == null ? "" : " (inh {0})".Fmt(InheritsFrom));
            }
        }

        private sealed class UnresolvedExtraProperty
        {
            public ExtraPropertyId PropertyId;
            /// <summary>Optional; empty if it wasn't specified.</summary>
            public Dictionary<string, string> Descriptions;
            /// <summary>Optional; null if it wasn't specified.</summary>
            public string InheritsFrom;

            public AutoDictionary<string, HashSet<ExtraEntry>> Entries;

            public override string ToString()
            {
                return PropertyId.ToString() + (InheritsFrom == null ? "" : " (inh {0})".Fmt(InheritsFrom));
            }

            public UnresolvedExtraProperty ImmediateParent;
            public HashSet<UnresolvedExtraProperty> TransitiveChildren = new HashSet<UnresolvedExtraProperty>();
            public int Depth;
        }

        private sealed class ResolvedExtraProperty
        {
            public ExtraPropertyId PropertyId { get; private set; }
            /// <summary>Optional; empty if it wasn't specified.</summary>
            public Dictionary<string, string> Descriptions { get; private set; }

            public Dictionary<string, List<ExtraEntry>> Entries { get; private set; }

            public ResolvedExtraProperty(ExtraPropertyId propertyId, Dictionary<string, string> descriptions, Dictionary<string, List<ExtraEntry>> entries)
            {
                PropertyId = propertyId;
                Descriptions = descriptions;
                Entries = entries;
            }

            public override string ToString()
            {
                return "Property " + PropertyId;
            }
        }

        #endregion

    }
}
