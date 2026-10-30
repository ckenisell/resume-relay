sub init()
    m.video = m.top.findNode("video")
    m.panel = m.top.findNode("panel")
    m.title = m.top.findNode("title")
    m.code = m.top.findNode("code")
    m.message = m.top.findNode("message")
    m.status = m.top.findNode("status")
    m.retry = m.top.findNode("retry")
    m.code.font.size = 150
    m.video.enableUI = false
    m.video.observeField("state", "onVideoState")
    ' Looping is done here: just before a looping video ends it's sent back to the start (seeking keeps the
    ' picture up, where letting it finish and starting again would show black).
    m.video.notificationInterval = 0.25
    m.video.observeField("position", "onPosition")
    m.retry.observeField("fire", "onRetry")
    m.current = invalid
    m.dialog = invalid
    m.lastReported = ""

    m.net = CreateObject("roSGNode", "NetTask")
    m.net.observeField("phase", "onPhase")
    m.net.observeField("code", "onPhase")
    m.net.observeField("message", "onPhase")
    m.net.observeField("video", "onVideo")
    m.net.control = "run"
    m.title.text = "Resume Relay Live Poster"
    m.top.setFocus(true)
end sub

sub onPhase()
    p = m.net.phase
    msg = m.net.message
    if p = "playing"
        m.panel.visible = false
        ' Only a lost connection is shown over the picture (pairing instructions never are).
        if msg = "Resume Relay isn't answering..."
            m.status.text = msg
            m.status.visible = true
        else if m.status.text = "Resume Relay isn't answering..."
            m.status.visible = false
        end if
        return
    end if
    m.panel.visible = true
    m.code.visible = (p = "code")
    if p = "code"
        m.title.text = "Add this screen"
        m.code.text = m.net.code
    else if p = "address"
        m.title.text = "Resume Relay's address"
    else
        m.title.text = "Resume Relay Live Poster"
    end if
    m.message.text = msg
    if p = "address" and m.dialog = invalid then askAddress()
end sub

sub onVideo()
    v = m.net.video
    if v = invalid then return
    if v.url = invalid or v.url = ""
        m.video.control = "stop"
        m.current = invalid
        return
    end if
    m.current = v
    play()
end sub

sub play()
    v = m.current
    if v = invalid then return
    c = CreateObject("roSGNode", "ContentNode")
    c.url = v.url
    c.streamFormat = "mp4"
    m.video.loop = false
    m.video.content = c
    m.video.control = "play"
end sub

sub onVideoState()
    s = m.video.state
    if s = "finished" and m.current <> invalid and not m.current.loop
        ' A slideshow round ended: Resume Relay hands over the next one (or the same one again).
        m.net.ended = m.current.version
    else if s = "finished" and m.current <> invalid
        play()   ' a looping video that ran out anyway
    else if s = "error"
        m.net.report = "error " + m.video.errorCode.ToStr() + ": " + m.video.errorMsg + " " + m.video.errorStr
        m.status.text = "Can't play this video (" + m.video.errorMsg + "). Trying again..."
        m.status.visible = true
        m.retry.control = "start"
    else if s = "playing"
        if Left(m.status.text, 15) = "Can't play this" then m.status.visible = false
        if m.current <> invalid and m.lastReported <> m.current.version
            m.lastReported = m.current.version
            m.net.report = "playing"
        end if
    end if
end sub

sub onPosition()
    if m.current = invalid or not m.current.loop then return
    d = m.video.duration
    if d <> invalid and d > 1 and m.video.position >= d - 0.6 then m.video.seek = 0
end sub

sub onRetry()
    play()
end sub

' ---------------------------------------------------------------- dialogs

sub askAddress()
    d = CreateObject("roSGNode", "StandardKeyboardDialog")
    d.title = "Resume Relay's address"
    d.message = ["The computer running Resume Relay, for example 10.0.0.30:8090"]
    server = m.net.server
    if server = invalid or server = "" then server = CreateObject("roRegistrySection", "rr").Read("server")
    if server <> invalid and Left(server, 7) = "http://" then server = Mid(server, 8)
    d.text = server
    d.buttons = ["Connect", "Search again"]
    d.observeField("buttonSelected", "onAddressButton")
    d.observeField("wasClosed", "onDialogClosed")
    m.dialog = d
    m.top.dialog = d
end sub

sub onAddressButton()
    d = m.dialog
    if d = invalid then return
    if d.buttonSelected = 0
        m.net.serverInput = d.text
    else
        m.net.command = "search"
    end if
    d.close = true
    m.dialog = invalid
end sub

sub showMenu()
    d = CreateObject("roSGNode", "StandardMessageDialog")
    name = m.net.screenName
    if name = invalid or name = "" then name = "Resume Relay"
    d.title = name
    server = CreateObject("roRegistrySection", "rr").Read("server")
    d.message = ["Connected to Resume Relay at " + Mid(server, 8) + ".", "What this screen shows is set on the Live Poster tab."]
    d.buttons = ["Close", "Change address", "Unpair this screen"]
    d.observeField("buttonSelected", "onMenuButton")
    d.observeField("wasClosed", "onDialogClosed")
    m.dialog = d
    m.top.dialog = d
end sub

sub onMenuButton()
    d = m.dialog
    if d = invalid then return
    b = d.buttonSelected
    d.close = true
    m.dialog = invalid
    if b = 1
        askAddress()
    else if b = 2
        m.net.command = "unpair"
    end if
end sub

sub onDialogClosed()
    m.dialog = invalid
    m.top.setFocus(true)
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    if not press then return false
    if key = "OK" and m.dialog = invalid
        showMenu()
        return true
    end if
    return false
end function
