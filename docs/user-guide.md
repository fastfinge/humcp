# humcp user guide

humcp is a Windows program that lets you use Model Context Protocol servers — MCP servers — directly, from the keyboard, with a screen reader. An MCP server is a small program that offers a set of tools: search a code repository, read a calendar, look up the weather, query a database. Most programs that talk to MCP servers hand the tools to an AI. humcp hands them to you.

You do not need to know what a server's tools look like in advance. Every server describes its own tools — their names, what they do, what they need from you — and humcp turns that description into a form you can fill in and a result you can read. Nothing in humcp is written for any particular server.

This guide is also inside humcp: choose User guide on the Help menu.

## Contents

1. Installing
2. Your first server
3. The main window
4. Running a tool
5. Reading a result
6. When a server asks you something
7. Managing servers
8. Settings
9. Keyboard reference
10. Where humcp keeps things, and privacy
11. Troubleshooting
12. Reporting a problem

## 1. Installing

Download humcp from the Releases page of the project on GitHub: https://github.com/fastfinge/humcp/releases

There are two ways to install it.

- The installer, humcp-x64.msi. Run it, accept the licence and choose Install; it needs no administrator rights and installs for you only. It adds humcp and its user guide to the Start menu, and Settings, Apps removes it. Anyone deploying it to many computers can install it silently with msiexec /i humcp-x64.msi /qn.
- The portable zip, humcp-win-x64.zip, or humcp-win-arm64.zip for ARM computers. Extract it anywhere and run Humcp.App.exe. Nothing is installed.

Windows may warn that humcp is from an unrecognised publisher, because the 1.0 release is not code-signed. In the SmartScreen window, choose More info, then Run anyway. Every release lists a SHA-256 checksum for each file, so you can check that what you downloaded is what was published.

humcp needs Windows 10 or 11. It works with any screen reader; it is developed and tested with NVDA.

## 2. Your first server

The first time humcp starts, the tree says no servers are configured. You need at least one server.

Many MCP servers are published as packages that run with npx (which comes with Node.js) or uvx (which comes with Python's uv). If you already use another MCP client, such as Claude Desktop or VS Code, you can bring its servers across in one step:

1. Copy the server settings from the other client. In Claude Desktop that is the mcpServers part of claude_desktop_config.json; in VS Code it is mcp.json. Copying the whole file is fine.
2. In humcp, open the File menu and choose Import servers.
3. Paste into the Configuration box and press Control plus Enter.

humcp says how many servers it imported and starts them.

To add one server by hand, press Control plus N. The form asks for:

- Name: what the tree calls it.
- Command: the program that starts it, such as npx. Or leave Command empty and give an Address instead, for a server reached over the web, starting with https://.
- Arguments: everything after the command, on one line. For example: -y @modelcontextprotocol/server-everything
- Working directory: usually empty.
- Environment variables: one NAME=value at a time, usually an API key the server needs. Type it, press Enter to add it to the list, and repeat. To change one, select it in the list and press F2 or the Edit button; it opens in a small window of its own, and Enter closes it with your change kept. Delete removes it.
- Start this server: ticked unless you want it in the list without running it.

Press Control plus Enter to save. humcp adds the server, puts the caret on it in the tree, and tells you when it has connected and how many tools it has.

## 3. The main window

The main window has two panes. F6 moves between them, and Shift plus F6 goes back.

- Servers and tools, on the left, is a tree. Each server is a top-level item, and its name tells you whether it is connected, how many tools it has, and the protocol version it speaks — or, if it could not start, why not. Under each server is its list of tools. A server with many tools that share a naming pattern has them grouped, for example all the issue tools together.
- Result, on the right, shows what the last tool you ran from the tree sent back.

Arrow keys move through the tree. Right arrow opens a group and Left arrow closes it. Typing a letter jumps to the next item starting with it. The status bar at the bottom says what humcp is doing, and your screen reader reads it as it changes.

Every window humcp opens has its own menu bar, reached with Alt, and a Window menu listing every open window. Control plus 1 through 9 switches straight to a window by its number. Only the main window appears in Alt plus Tab, so a long session does not bury your other programs under humcp's windows.

The servers pane sizes itself to fit what is in it. If you, or someone helping you, would rather it were a fixed size, choose Servers pane width on the View menu.

## 4. Running a tool

Arrow to a tool and press Enter.

- A tool with nothing to fill in runs straight away, and its result appears in the Result pane.
- A tool with parameters opens a form in a window of its own.

On a form, each field is announced with its name, whether it is required, and a short description. F1 on a field gives the full description, which servers often write at length. Many tools have far more optional settings than required ones, so optional fields start hidden; Control plus O shows them, and Settings can make that the default.

Text fields remember what you typed before, and offer it again in a drop-down: Alt plus Down arrow opens it. Anything whose name sounds like a password or a key is never remembered.

Press Control plus Enter to run. If humcp thinks something is wrong — a required field is empty, a number is out of range — it says so, lists the problems, and moves the caret to the first one. The server is the final judge, not humcp, so pressing Control plus Enter a second time runs the tool anyway.

While a tool runs, the window stays usable, and long-running tools report their progress every few seconds. Press Escape to stop a tool that is running; the server is told to stop. Press Escape again, with nothing running, to close the window.

A tool that says it can delete or change things that cannot be undone asks first: the first Enter or Control plus Enter tells you so, and the second runs it. This is the server's own description of itself, so it is a warning, not a guarantee in either direction.

## 5. Reading a result

A result is made of parts, and each part has a control suited to it.

- Text is in a read-only box. Arrow keys read it line by line, and Control plus Home goes to the top.
- A table is a grid with column headers. Arrow keys move from cell to cell, and each cell is read with its column's name. Shift plus F10, or the Applications key, on a column offers to sort by it; the column's header then says how it is sorted.
- Anything else structured is a tree, read as "name, value". Right arrow opens a branch.
- A picture, a sound or a file has Save and Open buttons. humcp decides the file type from what the server says it is, and never opens a file it does not recognise.
- A link has buttons to open it in your browser or copy it. humcp only opens web links; anything else can be copied but is not opened, and humcp says so.

In any part, Enter opens what the caret is on in a window of its own, and Control plus Shift plus C copies it. F1 shows everything about the result, including the raw data the server sent.

## 6. When a server asks you something

Some tools stop partway through to ask you a question, such as confirming a booking or choosing an option. The question opens in a window of its own, with the server's own wording first.

- Control plus Enter answers with what is on the form.
- Alt plus D declines: you are telling the tool no.
- Escape cancels: you are not answering at all.

Every field of a question is shown, never hidden, because a server is told about every one of them.

Some servers ask to send you to a web page to sign in or pay. humcp does not open those pages during a tool call, and shows you the address so you can decide for yourself.

## 7. Managing servers

All of these are on the File menu, and on the context menu of a server in the tree (Shift plus F10, or the Applications key).

- Add server, Control plus N.
- Import servers, for a block copied from another client.
- Server settings, Alt plus Enter: change a server's command, arguments, variables, or turn it off.
- Reconnect, F5: start the selected server again. Use this after a server has stopped.
- Remove, Delete: press it twice. The first press says what will be removed; the second removes it.

Edit, Copy server configuration copies the selected server in the same format other clients use, so you can paste it into another program.

If a server stops on its own — it crashed, or its computer went away — humcp says so, shows why in the tree, and keeps your open forms. F5 starts it again.

If a server changes its list of tools while you are using it, humcp updates the tree and tells you which tools were added or removed. A form for a tool that has gone says so in its title.

humcp connects to servers it starts itself and to servers on the web over HTTP. A web server that requires you to sign in is not supported yet; humcp says so rather than failing silently.

## 8. Settings

Choose Settings on the File menu, or press Control plus Comma.

- Show optional parameters when a form opens. Off by default.
- Remember what is typed into forms. On by default. Edit, Clear parameter history empties it.
- Speak when humcp is in the background. On by default. When another program is in front and a tool finishes, a server stops, or a server asks a question, humcp says so through your screen reader, because otherwise you would not hear it until you came back.

## 9. Keyboard reference

Help, Keyboard shortcuts shows this list inside humcp.

### Everywhere

- Alt: the menu bar.
- F6 and Shift plus F6: next and previous pane.
- Control plus 1 to 9: switch to that window.
- Control plus W or Escape: close the current window (not the main one).
- F1: full details of whatever the caret is on.

### The tree

- Enter: run the tool, or open its form.
- A letter: the next item starting with it.
- Control plus N: add a server.
- Alt plus Enter: settings of the selected server.
- F5: reconnect the selected server.
- Delete, twice: remove the selected server.
- Shift plus F10 or Applications: the context menu.
- Control plus Comma: settings.
- Escape: stop a tool that is running.

### A form

- Control plus Enter: run the tool.
- Control plus O: show or hide the optional parameters.
- Escape: stop the running tool; with nothing running, close the window.

### A list of entries in a form

- Enter in the entry box: add what is typed to the list.
- F2: change the selected entry, in a window of its own. Enter or Escape closes it, keeping the change.
- Delete: remove the selected entry.

### A result

- Enter: open what the caret is on in its own window.
- Control plus Shift plus C: copy it.
- Shift plus F10 or Applications in a table: sort by the current column.

### A question from a server

- Control plus Enter: accept. Alt plus D: decline. Escape: cancel.

## 10. Where humcp keeps things, and privacy

humcp keeps three files in your profile, in %APPDATA%\humcp:

- servers.json, your servers, in the same format other MCP clients use. You can edit it by hand; humcp changes only its own parts of it, and never overwrites a file it cannot read.
- settings.json, your settings.
- history.json, what you have typed into forms, for the drop-downs.

Environment variables, which are often API keys, are stored in servers.json in plain text, the same way every other MCP client stores them. Anyone who can read your profile can read them.

If humcp ever hits an error it did not expect, it writes the details to %LOCALAPPDATA%\humcp\errors.log and tells you.

humcp sends nothing anywhere except to the servers you configure. It has no telemetry, no update check and no account.

When humcp closes, or crashes, every server it started is stopped with it.

## 11. Troubleshooting

### A server says error in the tree

The rest of its name is the reason. Help, Connection diagnostics shows everything humcp knows about every server, including the last things the server itself printed, which usually say what is wrong: a missing program, a missing API key, a typing mistake in the command.

### A server that uses npx or uvx will not start

Node.js (for npx) or uv (for uvx) has to be installed and on your PATH. If it was installed after humcp was started, close humcp and start it again.

### One tool's form is a single box of text in curly brackets

That tool describes its parameters in a way humcp cannot turn into ordinary fields. The box holds JSON, the format the server expects, already shaped with the right names; fill in the values. The field's description says why it is shown this way.

### Nothing is spoken when a tool finishes while I am in another program

Check Settings, Speak when humcp is in the background. Help, Speech diagnostics says whether humcp can reach your screen reader.

## 12. Reporting a problem

Please report problems on GitHub: https://github.com/fastfinge/humcp/issues

The most helpful report says which server and tool you were using, what you pressed, what your screen reader said, and what you expected it to say. Help, Connection diagnostics has text you can copy into the report. If humcp told you it had saved error details, attach %LOCALAPPDATA%\humcp\errors.log.

Bug fixes are released as 1.x versions. New capabilities follow the project roadmap: version 2 adds a server's prompts and resources, long-running tasks, and optional AI help such as descriptions of images; version 3 adds signing in to servers that need it.
