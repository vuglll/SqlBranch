using SqlBranch.Cli;
using SqlBranch.Core.Secrets;

var redactor = new SecretRedactor();
return CliApp.CreateDefault(redactor).Run(args, Console.Out, Console.Error);
