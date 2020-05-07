#!/usr/bin/env python3
#chmod +x start.py & dos2unix start.py
#dotnet restore -v Debug
#dotnet -v run

import os
from subprocess import call
print('Python3: Change Directory')
#os.chdir("/home/ubuntu/SQ/WebServer/SQLab/src")
os.chdir("/home/ubuntu/SQ/WebServer/SQLab/src/WebServer/SQLab")
print('dotnet restore')
call(["dotnet", "restore"])
print('npm install  (restoring folder node_modules...)')
call(["npm", "install"])
#print('dotnet run --configuration Release')         # the default is the Debug configuration in the project.json file
#call(['dotnet', 'run', '--configuration', 'Release']) # before dotnet version #2777, `dotnet run` did Console.IsOutputRedirected, while 'dotnet build' not. That made Console.Colors fail.
print('dotnet build --configuration Release')         # the default is the Debug configuration in the project.json file
call(['dotnet', 'build', '--configuration', 'Release'])
#"type": "platform"  // if type = platform in project.json, the runtimes section ("ubuntu.14.04-x64") is not required. The current installed platform will be used for build. It is safer to use the installed RC2 than experiment with the latest ASP.Net components on Linux
print('sudo dotnet bin/Release/netcoreapp2.2/SQLab.dll')  # A normal user is not allowed to bind sockets to TCP ports < 1024, you need more permissions than the normal user
call(['sudo', 'dotnet', 'bin/Release/netcoreapp2.2/SQLab.dll'])     
k = input("Press ENTER...")       # raw_input is built but waiting for Enter key 