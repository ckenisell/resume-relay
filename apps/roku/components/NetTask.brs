sub init()
    m.top.functionName = "talk"
end sub

sub talk()
    m.port = CreateObject("roMessagePort")
    m.top.observeField("ended", m.port)
    m.top.observeField("serverInput", m.port)
    m.top.observeField("command", m.port)
    m.top.observeField("report", m.port)
    m.reg = CreateObject("roRegistrySection", "rr")
    m.server = m.reg.Read("server")
    m.token = m.reg.Read("token")
    m.version = ""
    m.seq = 0
    m.fails = 0
    m.wake = false
    while true
        m.wake = false
        if m.server = ""
            m.top.phase = "searching"
            m.top.message = "Looking for Resume Relay on the network..."
            if not discover()
                m.top.phase = "address"
                m.top.message = "Resume Relay wasn't found by itself. Enter the address of the computer running it, for example 10.0.0.30:8090."
                sleepFor(86400000)
            end if
        else if m.token = ""
            hello()
        else
            poll("")
            sleepFor(2000)
        end if
    end while
end sub

' ---------------------------------------------------------------- finding Resume Relay

' Broadcasts "RESUMERELAY_DISCOVER" to port 47090; Resume Relay answers with its port.
function discover() as boolean
    sock = CreateObject("roDatagramSocket")
    p = CreateObject("roMessagePort")
    sock.setMessagePort(p)
    sock.setBroadcast(true)
    addr = CreateObject("roSocketAddress")
    addr.setAddress("255.255.255.255:47090")
    sock.setSendToAddress(addr)
    sock.notifyReadable(true)
    for i = 1 to 3
        sock.sendStr("RESUMERELAY_DISCOVER")
        t = CreateObject("roTimespan")
        while t.TotalMilliseconds() < 1500
            msg = wait(1500 - t.TotalMilliseconds(), p)
            if type(msg) = "roSocketEvent" and sock.isReadable()
                s = sock.receiveStr(2048)
                j = ParseJson(s)
                if j <> invalid and j.app = "Resume Relay"
                    host = sock.getReceivedFromAddress().getHostName()
                    portNo = 8090
                    if j.port <> invalid then portNo = j.port
                    setServer(host + ":" + portNo.ToStr())
                    sock.close()
                    return true
                end if
            else if msg = invalid
                exit while
            end if
        end while
    end for
    sock.close()
    return false
end function

sub setServer(a as string)
    a = a.Trim()
    if Left(a, 7) = "http://" then a = Mid(a, 8)
    if Right(a, 1) = "/" then a = Left(a, Len(a) - 1)
    if a = "" then return
    if Instr(1, a, ":") = 0 then a = a + ":8090"
    m.server = "http://" + a
    m.reg.Write("server", m.server)
    m.reg.Flush()
    m.top.server = m.server
end sub

' ---------------------------------------------------------------- pairing

sub hello()
    di = CreateObject("roDeviceInfo")
    key = m.reg.Read("deviceKey")
    if key = ""
        key = "roku-" + di.GetRandomUUID()
        m.reg.Write("deviceKey", key)
        m.reg.Flush()
    end if
    app = CreateObject("roAppInfo")
    body = FormatJson({ deviceKey: key, platform: "roku", model: di.GetModelDisplayName() + " (" + di.GetModel() + ")", version: app.GetVersion() })
    r = http("POST", "/api/screen/hello", body)
    if r.json = invalid
        m.fails = m.fails + 1
        if m.fails >= 3
            m.fails = 0
            m.top.phase = "address"
            m.top.message = "Resume Relay didn't answer at " + Mid(m.server, 8) + ". Check that it's running, or enter its address."
            sleepFor(86400000)
        else
            m.top.phase = "connecting"
            m.top.message = "Connecting to Resume Relay at " + Mid(m.server, 8) + "..."
            sleepFor(3000)
        end if
        return
    end if
    m.fails = 0
    if r.json.paired = true
        m.token = r.json.token
        m.reg.Write("token", m.token)
        m.reg.Flush()
        m.top.screenName = r.json.name
        m.top.message = ""
        m.top.phase = "playing"
    else
        m.top.code = r.json.code
        m.top.message = "On a computer or phone, open Resume Relay at " + Mid(m.server, 8) + ", go to the Live Poster tab, and enter this code."
        m.top.phase = "code"
        sleepFor(3000)
    end if
end sub

' ---------------------------------------------------------------- playing

sub poll(ended as string)
    path = "/api/screen/state?token=" + m.token
    if ended <> "" then path = path + "&ended=" + ended
    r = http("GET", path, "")
    if r.code = 401
        m.token = ""
        m.reg.Delete("token")
        m.reg.Flush()
        m.version = ""
        m.seq = m.seq + 1
        m.top.video = { url: "", version: "", loop: true, seq: m.seq }
        return
    end if
    if r.json = invalid
        m.top.message = "Resume Relay isn't answering..."
        return
    end if
    m.top.message = ""
    if m.top.phase <> "playing" then m.top.phase = "playing"
    url = r.json.url
    v = r.json.version
    if url <> invalid and url <> "" and (v <> m.version or (ended <> "" and v = ended))
        m.version = v
        m.seq = m.seq + 1
        m.top.video = { url: m.server + url, version: v, loop: (r.json.loop = true), seq: m.seq }
    end if
end sub

' Waits up to ms, handling what the scene asks for meanwhile (a round that ended, a new address, the menu).
sub sleepFor(ms as integer)
    t = CreateObject("roTimespan")
    while t.TotalMilliseconds() < ms
        msg = wait(ms - t.TotalMilliseconds(), m.port)
        if msg = invalid then return
        if type(msg) = "roSGNodeEvent"
            f = msg.getField()
            d = msg.getData()
            if f = "ended" and m.token <> ""
                poll(d)
            else if f = "report" and m.token <> ""
                ' How playback is going, for the screen's card on the Live Poster tab.
                x = CreateObject("roUrlTransfer")
                http("GET", "/api/screen/report?token=" + m.token + "&msg=" + x.Escape(d), "")
            else if f = "serverInput"
                setServer(d)
                m.fails = 0
                return
            else if f = "command"
                if d = "unpair"
                    m.reg.Delete("token")
                    m.reg.Delete("deviceKey")
                    m.reg.Flush()
                    m.token = ""
                    m.version = ""
                    m.seq = m.seq + 1
                    m.top.video = { url: "", version: "", loop: true, seq: m.seq }
                else if d = "search"
                    m.server = ""
                end if
                return
            end if
        end if
    end while
end sub

' A JSON request to Resume Relay: { code, json } (json is invalid when it didn't answer).
function http(method as string, path as string, body as string) as object
    x = CreateObject("roUrlTransfer")
    p = CreateObject("roMessagePort")
    x.SetMessagePort(p)
    x.SetUrl(m.server + path)
    x.RetainBodyOnError(true)
    x.AddHeader("Content-Type", "application/json")
    if method = "POST"
        ok = x.AsyncPostFromString(body)
    else
        ok = x.AsyncGetToString()
    end if
    if not ok then return { code: 0, json: invalid }
    msg = wait(8000, p)
    if type(msg) <> "roUrlEvent"
        x.AsyncCancel()
        return { code: 0, json: invalid }
    end if
    code = msg.GetResponseCode()
    s = msg.GetString()
    j = invalid
    if code >= 200 and code < 300 and s <> "" then j = ParseJson(s)
    return { code: code, json: j }
end function
