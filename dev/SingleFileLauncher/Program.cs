using System.Diagnostics;
using System.IO.Compression;
using System.Text;

internal static class Program
{
    const string Release="shadow-reborn-51-hidden-output-tab";
    const string Footer="TSRBPACK0001";
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var mutex=new Mutex(true,"Local\\TenchuShadowRebornLauncher",out bool owns);
        if(!owns)return;
        string home=Environment.GetEnvironmentVariable("TENCHU_PACKAGE_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Tenchu Shadow Reborn");
        string folder=Path.Combine(home,"runtime"),marker=Path.Combine(folder,"package-version.txt");
        try
        {
            Directory.CreateDirectory(home);
            if(!File.Exists(marker)||File.ReadAllText(marker)!=Release)
            {
                using var progress=new Form {Text="Tenchu Shadow Reborn",ClientSize=new System.Drawing.Size(440,105),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterScreen,ControlBox=false};
                var label=new Label {Text="Preparando o jogo na primeira abertura…",Left=18,Top=18,Width=410};
                var bar=new ProgressBar {Left=18,Top=53,Width=404,Height=22};
                progress.Controls.Add(label);progress.Controls.Add(bar);progress.Show();Application.DoEvents();
                using var file=File.OpenRead(Environment.ProcessPath!);
                file.Seek(-(Footer.Length+8),SeekOrigin.End);
                using var reader=new BinaryReader(file,Encoding.ASCII,true);
                long length=reader.ReadInt64();
                if(Encoding.ASCII.GetString(reader.ReadBytes(Footer.Length))!=Footer || length<=0 || length>file.Length-Footer.Length-8)throw new InvalidDataException("Pacote incompleto. Copie novamente o EXE inteiro.");
                using var slice=new SliceStream(file,file.Length-Footer.Length-8-length,length);
                using var zip=new ZipArchive(slice,ZipArchiveMode.Read);
                int done=0;
                foreach(var entry in zip.Entries)
                {
                    string target=Path.GetFullPath(Path.Combine(folder,entry.FullName));
                    if(!target.StartsWith(Path.GetFullPath(folder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Caminho invalido no pacote.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // Existing player progress survives reinstalling/updating the package.
                    bool save=entry.FullName.Replace('\\','/').Contains("/saves/",StringComparison.OrdinalIgnoreCase);
                    if(!(save && File.Exists(target)))entry.ExtractToFile(target,true);
                    done++;bar.Value=(int)(done*100L/zip.Entries.Count);Application.DoEvents();
                }
                File.WriteAllText(marker,Release);
                progress.Close();
            }
            string game=Path.Combine(folder,"Tenchu-Shadow-Reborn","Tenchu Shadow Reborn.exe");
            if(args.Contains("--verify-only")) {File.WriteAllText(Path.Combine(home,"verification.txt"),File.Exists(game)?"OK":"MISSING");return;}
            var info=new ProcessStartInfo(game){WorkingDirectory=Path.GetDirectoryName(game)!,UseShellExecute=false};
            foreach(var key in info.Environment.Keys.Where(k=>k.StartsWith("TENCHU_TEST_",StringComparison.OrdinalIgnoreCase)||k=="TENCHU_BENCHMARK_SECONDS").ToArray())info.Environment.Remove(key);
            info.Environment.Remove("CHAIN_DELAY");
            info.Environment["TENCHU_QUICK_TEST"]="0";
            info.Environment["TENCHU_QUICK_MENU"]="0";
            info.Environment["TENCHU_FULLSCREEN"]="0";
            info.Environment["TENCHU_STRETCH"]="1";
            info.Environment["TENCHU_TEXTURE_PACK"]="1";
            info.Environment["TENCHU_REFINED_RIKIMARU"]="1";
            info.Environment["TENCHU_HD_RIKIMARU"]="0";
            info.Environment["TENCHU_DISC_PATH"]=Path.Combine(folder,"Tenchu-PC","disc","Tenchu - Stealth Assassins (USA).cue");
            info.Environment["TENCHU_TEXTURE_PACK_PATH"]=Path.Combine(folder,"texture-packs","tenchu-native");
            using var process=Process.Start(info);
            process?.WaitForExit();
        }
        catch(Exception ex)
        {
            Directory.CreateDirectory(home);File.WriteAllText(Path.Combine(home,"launcher-error.txt"),ex.ToString());
            MessageBox.Show("Nao foi possivel abrir o jogo.\n\n"+ex.Message,"Tenchu Shadow Reborn",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
        finally{mutex.ReleaseMutex();}
    }
    sealed class SliceStream(Stream source,long start,long length):Stream
    {
        long position;
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
        public override long Length=>length;
        public override long Position{get=>position;set{if(value<0||value>length)throw new IOException();position=value;}}
        public override int Read(byte[] buffer,int offset,int count){source.Position=start+position;int read=source.Read(buffer,offset,(int)Math.Min(count,length-position));position+=read;return read;}
        public override long Seek(long offset,SeekOrigin origin){Position=offset+(origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:length);return position;}
        public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}




