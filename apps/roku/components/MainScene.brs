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
    m.demo = false
    m.demoGroup = m.top.findNode("demo")
    m.demoA = m.top.findNode("demoA")
    m.demoB = m.top.findNode("demoB")
    m.demoFade = m.top.findNode("demoFade")
    m.demoFadeB = m.top.findNode("demoFadeB")
    m.demoTimer = m.top.findNode("demoTimer")
    m.demoTimer.observeField("fire", "onDemoTick")
    m.demoFade.observeField("state", "onDemoFaded")
    m.lastReported = ""

    m.net = CreateObject("roSGNode", "NetTask")
    m.net.observeField("phase", "onPhase")
    m.net.observeField("code", "onPhase")
    m.net.observeField("message", "onPhase")
    m.net.observeField("video", "onVideo")
    m.net.observeField("loading", "onLoading")
    m.loadingBg = m.top.findNode("loadingBg")
    m.loadingBox = m.top.findNode("loadingBox")
    m.loadingFill = m.top.findNode("loadingFill")
    m.net.control = "run"
    m.title.text = "Resume Relay Live Poster"
    m.top.setFocus(true)
end sub

sub onPhase()
    if m.demo then return   ' the demo covers everything until it's stopped (then this runs again)
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

' ---------------------------------------------------------------- demo

' A built-in slideshow (7 invented posters on the built-in backgrounds, each dissolving into the next), so the app can
' be tried without Resume Relay. demoB sits over demoA: it fades in with the next poster, or out to show demoA's.
sub startDemo()
    m.demo = true
    m.demoIndex = 1
    m.demoA.uri = demoUri(1)
    m.demoB.opacity = 0
    m.demoB.uri = demoUri(2)
    m.panel.visible = false
    m.status.visible = false
    m.demoGroup.visible = true
    m.demoTimer.control = "start"
end sub

sub stopDemo()
    m.demo = false
    m.demoTimer.control = "stop"
    m.demoFade.control = "stop"
    m.demoGroup.visible = false
    m.demoA.uri = ""
    m.demoB.uri = ""
    onPhase()
end sub

function demoUri(i as integer) as string
    return "pkg:/images/demo/" + (((i - 1) mod 7) + 1).ToStr() + ".jpg"
end function

sub onDemoTick()
    if not m.demo then return
    m.demoIndex = m.demoIndex + 1
    if m.demoB.opacity > 0.5
        m.demoFadeB.keyValue = [1.0, 0.0]   ' demoA (already holding the next poster) shows through
    else
        m.demoFadeB.keyValue = [0.0, 1.0]   ' demoB (already holding the next poster) fades in
    end if
    m.demoFade.control = "start"
end sub

' Once a dissolve is done, the poster now hidden is loaded with the one after, ready for the next dissolve.
sub onDemoFaded()
    if not m.demo or m.demoFade.state <> "stopped" then return
    if m.demoB.opacity > 0.5
        m.demoA.uri = demoUri(m.demoIndex + 1)
    else
        m.demoB.uri = demoUri(m.demoIndex + 1)
    end if
end sub

' "Loading..." while the first slideshow is made. The posters arrive already turned for a TV on its side, so the
' words are turned the same way (positive rotation is a quarter turn to the left, like the posters for "right").
sub onLoading()
    l = m.net.loading
    if l = invalid then return
    on = (l.on = true)
    m.loadingBg.visible = on
    m.loadingBox.visible = on
    if not on then return
    if l.mount = "left"
        m.loadingBox.rotation = -1.5708
    else if l.mount = "portrait"
        m.loadingBox.rotation = 0
    else
        m.loadingBox.rotation = 1.5708
    end if
    p = l.progress
    if p = invalid then p = 0
    if p < 0 then p = 0
    if p > 1 then p = 1
    m.loadingFill.width = 700 * p
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
    d.buttons = ["Connect", "Search again", "Try a demo"]
    d.observeField("buttonSelected", "onAddressButton")
    d.observeField("wasClosed", "onDialogClosed")
    m.dialog = d
    m.top.dialog = d
end sub

sub onAddressButton()
    d = m.dialog
    if d = invalid then return
    b = d.buttonSelected
    if b = 0
        m.net.serverInput = d.text
    else if b = 1
        m.net.command = "search"
    end if
    d.close = true
    m.dialog = invalid
    if b = 2 then startDemo()
end sub

sub showMenu()
    d = CreateObject("roSGNode", "StandardMessageDialog")
    name = m.net.screenName
    if name = invalid or name = "" then name = "Resume Relay"
    d.title = name
    server = CreateObject("roRegistrySection", "rr").Read("server")
    if m.demo
        d.title = "Resume Relay Live Poster"
        d.message = ["This is a demo with invented posters.", "To show your own, run Resume Relay on a computer at home (free, from github.com/ckenisell/resume-relay) and add this screen on its Live Poster tab."]
        d.buttons = ["Close", "Stop the demo"]
    else if m.net.phase = "playing"
        d.message = ["Connected to Resume Relay at " + Mid(server, 8) + ".", "What this screen shows is set on the Live Poster tab."]
        d.buttons = ["Close", "Change address", "Unpair this screen", "Try a demo"]
    else
        d.message = ["This screen shows posters from Resume Relay, a free app that runs on a computer at home.", "Not set up yet? Try the demo."]
        d.buttons = ["Close", "Change address", "Unpair this screen", "Try a demo"]
    end if
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
    if m.demo
        if b = 1 then stopDemo()
    else if b = 1
        askAddress()
    else if b = 2
        m.net.command = "unpair"
    else if b = 3
        startDemo()
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
