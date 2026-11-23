<p align="center"><img src="docs/icon.png" width="96" alt="agentprobe"></p>
<h1 align="center">agentprobe</h1>
<p align="center">
  AI 코딩 에이전트가 같은 네트워크의 <b>다른 Windows PC</b>를 디버깅하고 테스트하게 해 줍니다.<br>
  실행 파일 하나. 테스트할 PC에는 설치도, SSH도, 계정도, 방화벽 설정도 필요 없습니다.
</p>
<p align="center"><a href="README.md">English</a> · <b>한국어</b></p>

---

"그 PC에서만 문제가 생겨요." 보통은 그 PC에 SSH 서버를 설치하고, 방화벽 포트를 열고, 계정 정보를 넘기고,
끝나면 그걸 전부 되돌려야 합니다. **agentprobe**는 이 과정을 한 단계로 줄입니다.

1. 문제가 있는 PC에 `agentprobe.exe`를 복사해서 더블클릭하고 **이 PC 공유**를 고릅니다.
2. 같은 네트워크의 개발 PC를 스스로 찾습니다. 그 PC 앞에 있는 사람이 **Enter**를 눌러 허용합니다.
3. 개발 PC에서 에이전트(Claude Code 등)나 사람이 `agentprobe ...` 명령을 실행하면, 그 명령이 저쪽 PC에서 돕니다.

테스트한 PC에서 창을 닫으면 끝입니다. 설치된 것은 없습니다.

> **이 페이지를 사용자가 알려 줘서 읽고 있는 AI 에이전트라면:** 바로 사용할 수 있습니다.
> 1. 이 PC에 `agentprobe`가 없으면 이 저장소의 최신 릴리스에서 `agentprobe.exe`를 받아(파일 하나, Windows x64,
>    설치 프로그램 없음) 경로를 지정해 실행하세요.
> 2. `agentprobe guide`를 실행하고 그대로 따르세요. `agentprobe list`가 작업할 수 있는 PC를 보여 줍니다.
> 3. 목록에 PC가 없으면, 사용자에게 같은 exe를 문제가 있는 PC에 복사해 실행하고 승인해 달라고 요청하세요.

```
 개발 PC                                            테스트할 PC
 에이전트 ──▶ agentprobe exec / ps / watch / ...    agentprobe.exe (더블클릭)
                 │                                         │
           백그라운드 데몬  ◀──── 암호화된 TCP ────────────┘   테스트할 PC가 바깥으로 연결하므로
           (알아서 뜸)                                          열어 둘 포트가 없습니다
```

## 빠른 시작

**개발 PC에서** (처음 한 번):

`agentprobe.exe`를 계속 둘 폴더에 놓고, 그 폴더에서 터미널을 열어 실행합니다.

```powershell
.\agentprobe.exe setup
```

이 폴더를 사용자 `PATH`에 등록해서, 새로 여는 터미널에서는 사람도 에이전트도 `agentprobe`만 입력하면 되게 합니다.
또 테스트할 PC가 이 PC를 찾을 수 있도록 Windows 방화벽에 TCP/UDP 48650을 엽니다(관리자 승인 한 번).
`agentprobe setup --remove`로 둘 다 되돌립니다.

**테스트할 PC에서:** `agentprobe.exe`를 복사해 실행합니다. 찾은 개발 PC가 표시됩니다.

```
  [1] DEV-PC  (user alex)  192.168.0.20  key 3F9A-21C4-88D0-B7E1

Let DEV-PC run commands on this PC with the rights of tester? [Y/n]
```

**다시 개발 PC에서:**

```powershell
agentprobe list                         # 연결된 PC 목록
agentprobe triage                       # 그 PC에 지금 무슨 문제가 있는지 한눈에
agentprobe exec 'Get-Service Spooler'   # 무엇이든 실행 (기본은 PowerShell)
agentprobe guide                        # AI 에이전트가 먼저 읽어야 할 사용 안내
```

일반 명령줄 도구이므로 **셸 명령을 실행할 수 있는 에이전트라면 무엇이든** 쓸 수 있습니다. Claude Code, Codex,
Gemini CLI, Cursor 등 어느 것이든 등록 절차가 없습니다. 에이전트가 읽는 지침 파일(`CLAUDE.md`, `AGENTS.md` 등)에
한 줄만 넣으면 됩니다.

```markdown
To debug or test on the remote Windows PC, use the `agentprobe` CLI. Run `agentprobe guide` first.
```

안내서는 에이전트 종류와 상관없이 하나이고 exe 안에 들어 있습니다. `agentprobe guide`가 출력하고, 대시보드의
**에이전트 연결** 버튼이 복사할 한 줄과 함께 보여 줍니다. 사람도 터미널에서 같은 명령을 쓰거나, 아래의 대시보드를 씁니다.

### MCP로 설정하는 에이전트라면

같은 exe가 MCP 서버(stdio)이기도 합니다.

```powershell
claude mcp add agentprobe -- "C:\tools\agentprobe.exe" mcp      # Claude Code
codex mcp add agentprobe -- "C:\tools\agentprobe.exe" mcp       # Codex
```

다른 클라이언트에서는 명령 `agentprobe.exe`에 인자 `mcp` 하나를 주어 등록하면 됩니다.

MCP의 도구 정의는 매 턴마다 에이전트의 컨텍스트를 차지하므로 서버를 일부러 얇게 만들었습니다. 도구는
`list_targets`, `run`(agentprobe 명령을 그대로 실행), `screenshot`(이미지로 반환), `guide` 네 개이고 합쳐서 약 2KB입니다.
서버의 안내문이 exe 위치를 알려 주므로, 셸을 쓸 수 있는 에이전트는 출력을 걸러 내고 파일로 저장할 수 있는
명령줄로 넘어갑니다. MCP로도 빠지는 기능은 없습니다. `run`이 이 문서의 모든 명령을 실행합니다.

테스트할 PC는 연결된 모든 네트워크에서 개발 PC를 찾습니다. 브로드캐스트를 보내고, /22(주소 1022개) 이하인
서브넷에는 주소마다 따로 probe를 보냅니다. VPN 터널은 브로드캐스트도 멀티캐스트도 전달하지 않으므로, VPN 너머의
개발 PC는 이 방법으로 찾습니다. `agentprobe share --dry-run`은 아무것도 공유하지 않고, 어디를 찾는지와 어느 개발 PC가 응답하는지만 보여 줍니다.
라우터 너머나 더 큰 VPN에 있는 개발 PC는 이렇게 찾을 수 없으니 주소를 직접 줍니다: `agentprobe share 192.168.0.20`

## SSH와 무엇이 다른가

| | SSH | agentprobe |
|---|---|---|
| 테스트할 PC 준비 | OpenSSH 서버 설치, 22번 포트 열기, 계정 생성·공유 | exe 하나 복사해서 실행 |
| 끝난 뒤 정리 | 제거, 포트 닫기, 키 삭제 | 창 닫기 |
| PC 찾기 | 주소를 알아야 함 | 같은 네트워크면 자동 |
| GUI 프로그램 | 보이지 않음 (세션 0) | 로그인한 사용자의 화면에서 실행. 보이고, 캡처되고, 클릭됨 |
| AI 에이전트의 비용 | 셸 출력 그대로: 길고 여백이 많아 한 번 볼 때마다 수천 토큰 | 용도별 명령이 몇 줄로 답함 |
| 모니터링 | 반복해서 조회 | `watch`가 백그라운드에서 기록하고 추세를 알려 줌 |
| 연결이 끊기면 | 실행 중이던 명령도 같이 죽음 | 명령은 계속 돌고, `history`·`result`로 이어받음 |
| 그 PC 앞의 사람 | 아무것도 알 수 없음 | 화면 배지, 실시간 활동 목록, 로그 파일 |

## 에이전트에 맞춘 설계: 토큰은 적게, 왕복은 적게

- **답이 짧습니다.** `agentprobe proc MyApp`은 열 줄입니다. 경로, 명령줄, 사용자, 비트 수, 메모리와 최고치,
  핸들, GDI/USER 개체, 창, 자식 프로세스가 들어 있습니다. 같은 내용을 PowerShell로 얻으려면 코드 한 문단에
  출력 한 페이지가 필요합니다.
- **걸러내기는 테스트할 PC에서 합니다.** `--grep`, `--tail`, `--since`, `--top`으로 필요한 줄만 돌아옵니다.
- **새로 생긴 것만 읽습니다.** `logs`, `dbg`, `read --new`는 직전 호출 이후에 추가된 부분만 돌려줍니다.
- **표본이 아니라 추세를 줍니다.** `watch`는 한 번의 호출에 "private 210M→480M, 분당 +22M, 꾸준히 증가:
  누수 가능성 높음"처럼 한 줄로 답합니다.
- **기다리는 명령이 있습니다.** `wait --window "Setup" --timeout 60s`가 반복 조회를 대신합니다.
- **출력에 상한이 있습니다.** 긴 출력은 앞 200줄과 뒤 100줄만 보여 주고, 전체는 로컬 파일에 저장해 검색하게 합니다.
- **여러 대는 묶어서 보여 줍니다.** `-t all`로 50대에 실행하면 같은 결과는 한 번만 출력하고 해당 PC 목록을 붙입니다.

## 할 수 있는 일

| 영역 | 명령 |
|---|---|
| 실행 | `exec` (PowerShell, pwsh, 32비트 PowerShell, cmd. `exec -f script.ps1 -Arg 값`은 스크립트를 인자와 함께 보냄), 백그라운드 작업 `start` / `jobs` / `logs` / `kill` (`start --exe app.exe`는 GUI 앱 자체를 작업으로 추적, `start --lnk`는 바로가기가 실행하는 그대로 실행, `--env 이름=값`으로 환경 변수 지정, `--replace MyApp.exe`는 실행 중인 것을 먼저 닫음), 프로세스·창·포트·파일·로그 줄·CPU 유휴를 기다리는 `wait` |
| 파일 | `push`, `pull` (파일과 폴더), `ls`, `read` (tail, grep, 줄 범위, 로그의 시간 구간, 새 줄만), `stat` (버전, x86/x64, .NET 여부, SHA-256, 인터넷 다운로드 차단 표시, 바로가기가 실행하는 것), `locks` (파일을 잡고 있는 프로세스) |
| 첫 진단 | `triage`: 부하, 응답 없는 창, 최근 크래시와 오류, 디스크 부족, 재부팅 대기, 그리고 다음에 볼 것. 실행 중이 아닌 앱은 설치된 위치와 실행 방법을 알려 줌 |
| 프로세스 | `ps`, `top`, `proc`, `threads` (스레드별 CPU, 대기 이유, 이름, 만든 시각, 시작 모듈. 이름별 스레드 수), `stacks` (실행 중인 프로세스의 콜스택, 같은 스택끼리 묶음), `modules` (로드된 DLL의 버전과 경로), `handles` (종류별 개수와 증가분), `vmmap` (어떤 종류의 메모리가 늘어나는지: 힙, 스택, 이미지, 매핑 파일), `heap` (.NET 객체를 형식별로 세고, 원하면 아직 참조되는 것만 세며, 살아 있는 창 객체와 그중 화면에 떠 있는 개수, 하나를 살려 두는 참조 경로, 객체의 필드 값을 보여 줌. 테스트할 PC에서 스냅샷으로 읽음), `dump` (소형 또는 전체 메모리 덤프를 개발 PC로 가져오거나 그 PC에 남김) |
| 시스템 | `sys` (OS 빌드, 하드웨어, 로캘, GPU 드라이버, 디스플레이, .NET·VC++ 런타임), `mem` (커밋, 풀, 대기 목록, 메모리를 많이 쓰는 프로그램), `net` (리스닝 포트, 프로세스별 연결, 도달 가능 여부), `svc`, `reg`, `firewall` (올린 프로그램이 세션 동안 연결을 받도록 허용하거나, Windows 방화벽 창을 닫음) |
| 왜 그 PC만? | `compare <pc> local` (OS, 드라이버, 런타임, 환경 변수, PATH, 설치 프로그램, 서비스, 파일의 차이), `envsnap --save/--diff` (설치 프로그램이나 업데이트가 바꾼 것), `deps` (없는 DLL, x86/x64 불일치, 없는 함수, 없는 .NET 런타임. .NET 앱은 호스트가 필요로 하는 것도 확인: 앱 DLL, `.deps.json`과 거기 적힌 파일. `deps --run`은 호스트 추적을 켜고 한 번 실행해 호스트가 왜 포기했는지 알려 줌) |
| 로그 | `events` (Windows 이벤트 로그, 필터·중복 제거), `crashes` (문제 모듈, 예외 코드, 덤프 파일, 블루스크린), `dbg` (OutputDebugString 캡처, DebugView 역할) |
| 모니터링 | `watch`: 프로세스별 private 바이트, 핸들, GDI/USER 개체, 스레드, CPU의 증가율과 누수 판정. 지켜보는 프로세스와 그 프로세스가 띄운 것의 시작·종료(종료 코드 해석 포함), 모든 크래시, 응답을 멈춘 창. Windows가 자기 도우미 프로세스를 띄우고 닫는 잡음은 건수만 표시 |
| 캡처 | `trace` (프로세스의 파일·레지스트리 접근 중 실패한 것만 집계: Process Monitor에서 가장 쓸모 있는 화면을 짧은 표로), `crashdumps on` (앱이 죽을 때마다 Windows가 덤프 저장), `wpr start/stop` (Windows Performance Recorder 추적: CPU, 디스크, 파일, 레지스트리, 힙) |
| 화면 | `ui` (창의 컨트롤을 선택·체크·펼침 상태, 스크롤 위치, 슬라이더 값과 함께 글자로 읽고, 이름으로 누르고 터치하고 읽고 채움. `ui --at @x,y`는 그 지점에 있는 것), `windows` (사람의 응답을 기다리는 Windows 프롬프트와 열려 있는 메뉴도 표시: 방화벽, UAC, 자격 증명), `screenshot` (전체, 모니터, 창 하나, 축소), `click` (클릭을 받은 창을 알려 주고, 지정한 창을 다른 창이 가리고 있으면 누르지 않음), `touch` (손가락처럼 탭, 두 번 탭, 길게 누르기, 끌기. 터치 화면이 없는 PC에서도 동작), `drag`, `scroll`, `type`, `key`, `focus` |
| 세션 | `list` (각 PC가 하고 있는 일, 연결된 시각, 그 PC의 사람이 일시 멈춤을 걸었는지), `dashboard`, `name`, `history`, `result`, `reboot --wait`, `end` |

`agentprobe help`가 전체 목록을, `agentprobe help <명령>`이 옵션을 보여 줍니다.

### 누수 추적 예시

```powershell
agentprobe push .\bin\Release build            # 빌드가 세션 작업 폴더에 올라갑니다
agentprobe watch start MyApp
agentprobe start --name app '.\build\MyApp.exe'
agentprobe exec -f .\stress-scenario.ps1
agentprobe watch
```
```
watching MyApp, every 2s for 12m03s (361 samples)
system   cpu avg 12% max 97%   ram 57%->61%   commit 70%->74%
MyApp.exe(5120)  private 210M->480M (+22.4M/min, steady growth: LIKELY LEAK)  handles 812->4310 (+291/min, steady growth: LIKELY LEAK)  gdi 120  user 85  threads 24->25
events since the last report (2):
  14:01:03 start helper.exe(4321) by MyApp.exe(5120)
  14:01:05 exit  helper.exe(4321) code 0xC0000005 access violation after 2.1s
```
```powershell
agentprobe handles MyApp --interval 30s        # 어떤 종류의 핸들이 늘어나는지
agentprobe dump MyApp --full -o .\dumps\       # WinDbg / Visual Studio / dotnet-dump로 분석
```

## PC가 여러 대일 때

- `agentprobe name -t LAB-PC07 gpu-rig`로 PC에 짧은 이름을 붙입니다. 이름은 개발 PC에 기억됩니다.
  테스트할 PC 쪽에서 `agentprobe share --name gpu-rig`로 스스로 이름을 정할 수도 있습니다.
  `agentprobe share --minimized`는 개발 PC가 연결되면 자기 창을 작업 표시줄로 내려, 테스트할 앱을 가리지 않게 합니다.
- `-t gpu-rig`, `-t a,b,c`, `-t "LAB-*"`, `-t all`. 여러 대를 지정하면 같은 결과끼리 묶입니다.
  ```
  == 47 targets (exit 0): LAB-PC01, LAB-PC02, ... +35 more
  10.0.26200.1742
  == 3 targets (exit 0): LAB-PC11, LAB-PC12, LAB-PC30
  10.0.19045.5011
  ```
- `agentprobe dashboard`는 사람이 보는 실시간 웹 페이지를 엽니다.
  - **Screens**: PC마다 타일 하나씩, 화면 썸네일과 상태·CPU·RAM을 한 화면에 늘어놓습니다. 타일 크기와 갱신 주기
    (5초~1분, 또는 일시 정지)를 조절할 수 있습니다. 화면에 보이는 타일만 갱신하고 한 번의 갱신은 작은 캡처
    한 장이므로, 100대를 지켜봐도 대상 PC가 느려지지 않습니다.
  - **List**: 눈으로 훑기에는 너무 많은 PC를 위한 정렬·검색 표.
  - PC별 **상세 패널**: 상태 요약, 시스템 정보, 더 큰 실시간 화면, 프로세스, 그 PC에서 실행된 명령 이력,
    그리고 그 PC에 agentprobe 명령을 바로 실행하는 **콘솔**.
  - 밝은/어두운 테마, 단축키 (`/` 검색, `g` 화면, `l` 목록, `t` 테마, `Esc` 닫기).

  페이지는 `127.0.0.1`에서만 열리고 무작위 토큰으로 보호됩니다. 대시보드에서 PC 화면을 보면 그 PC의 활동 로그에
  기록됩니다.
- `agentprobe list --all`은 연결이 끊긴 PC도 보여 줍니다. 마지막으로 본 시각과 하던 일이 함께 나옵니다.

exe를 더블클릭하면 이 PC가 어느 쪽인지 묻습니다. `1`은 이 PC를 테스트 대상으로 공유하고, `2`는 개발 PC로 써서
시작 화면을 보여 줍니다. Enter는 지난번 역할을 그대로 고릅니다(개발 PC로 쓴 적이 있으면 개발 PC, 아니면 공유).
`agentprobe share`와 `agentprobe home`은 묻지 않고 바로 그 역할로 시작합니다.

시작 화면은 클릭할 줄 몇 개로 되어 있습니다. 네 줄은 에이전트에게 줄 것을 복사합니다. 지침 파일에 넣을 한 줄,
이 exe의 경로가 이미 채워진 `claude mcp add` / `codex mcp add` 명령, 대화에 붙여 넣을 문구입니다. 나머지는 대시보드
열기, 연결된 PC 목록, 이 PC 공유, 처음 한 번 하는 설정, 그리고 새 버전이 있을 때의 업데이트 설치입니다. 줄을
클릭하거나 숫자 키를 누르면 되고(마우스를 올리면 강조됩니다), 연결된 PC 목록은 실시간으로 바뀝니다. 창 맨 아래에는 명령 입력줄이 고정되어 있어
다른 터미널을 열지 않고 `list`, `help`, `-t <이름> triage` 같은 명령을 바로 실행할 수 있습니다. 창 크기에 맞춰 다시
그려지며 Windows Terminal과 기존 콘솔 창, cmd와 PowerShell 모두에서 동작합니다. 파이프로 연결하면(에이전트가
`agentprobe home`을 호출하는 경우) 같은 내용을 일반 텍스트로 출력합니다. 거기서 `share`를 입력하면 이 PC를 공유하는
창이 새로 열립니다.

더블클릭으로 연 시작 화면을 닫으면 agentprobe의 백그라운드 프로세스도 함께 끝납니다. 단, 연결된 PC가 있거나,
다른 agentprobe 명령이 실행 중이거나, 최근 90초 안에 대시보드 페이지가 무언가를 요청했으면 남겨 둡니다. 다음 `agentprobe` 명령이 다시 시작합니다. 터미널에서는
`agentprobe stop --if-idle`이 같은 일을 하고, `agentprobe stop`은 언제나 끝냅니다.

## 업데이트

```powershell
agentprobe update --check        # 새 릴리스가 있는지
agentprobe update                # 이 PC에 설치
agentprobe -t LAB-PC01 update    # 연결된 PC를 이 PC와 같은 버전으로
```

agentprobe는 exe 하나이고, 업데이트한 뒤에도 그대로입니다. 개발 PC의 백그라운드 프로세스가 하루 두 번 이 저장소의
최신 릴리스를 확인합니다(`AGENTPROBE_NO_UPDATE_CHECK=1`로 끌 수 있고, 테스트 대상 PC는 스스로 확인하지 않습니다).
새 버전이 있으면 시작 화면에 클릭할 줄이, 대시보드에 버튼이 생기고 `agentprobe list`도 한 줄로 알려 줍니다.
설치하면 릴리스에서 `agentprobe.exe`와 `.sha256`을 내려받아 체크섬이 맞고 exe가 실행되는지 확인한 다음, 실행 중인
exe를 옆으로 옮기고(실행 중인 파일은 덮어쓸 수 없지만 이름은 바꿀 수 있습니다) 그 자리에 새 exe를 놓고 백그라운드
프로세스를 새 exe로 다시 시작합니다. 옮겨 둔 파일은 그 파일로 도는 프로세스가 없어진 뒤(늦어도 agentprobe를 다음에 시작할 때) 지우므로
파일이 하나 더 남지 않습니다. 열려 있던 대시보드 페이지는 새 버전으로 스스로 다시 열립니다.

연결된 PC의 세션은 그대로 이어집니다. 몇 초 안에 스스로 다시 연결되고, 거기서 돌던 명령도 계속 돕니다. 다만 개발
PC에서 결과를 기다리던 명령은 "connection lost"와 실행 번호를 남기고 끝납니다(`agentprobe result <번호>`로 결과를
받습니다). 그래서 실행 중인 명령이 있으면 이 점을 설명하고 종료 코드 4로 끝난 뒤 `--force`나 한 번 더 클릭을 기다립니다.

테스트 대상 PC는 인터넷이 아니라 개발 PC에서 업데이트합니다. `agentprobe -t <이름> update`가 이 PC의 exe를 기존
연결로 보내고, 그 PC에서 agentprobe가 새 창으로 다시 시작합니다. 세션, 승인, 작업 폴더는 그대로 넘어갑니다. 거기서
돌던 백그라운드 작업과 명령은 넘어가지 않으므로, 그런 것이 있을 때도 `--force`를 요구합니다. 아직 0.1인 대상 PC는
이번 한 번만 새 exe를 직접 복사해야 합니다.

체크섬은 exe와 같은 릴리스에서 옵니다. 깨진 다운로드는 막지만, 변조된 릴리스까지 막아 주지는 않습니다. 조직 안에서
빌드를 배포한다면 `AGENTPROBE_UPDATE_URL`로 자체 미러(https만 허용)를 가리키게 할 수 있습니다.

## 재부팅

```powershell
agentprobe reboot --wait --timeout 10m
```

테스트할 PC는 **일회용** 자동 시작을 등록하고 재부팅한 뒤, 같은 사용자가 다시 로그온하면 묻지 않고 다시 연결합니다.
서비스는 설치하지 않습니다. 서비스는 세션 0에서 SYSTEM 계정으로 돌아서 GUI 프로그램이 보이지 않고 사용자 프로필도
로드되지 않으므로, 사용자의 문제를 재현하기에 맞지 않는 자리입니다. 자동 시작 항목은 스스로 지워집니다.
무인 PC는 Windows 자동 로그온이 필요합니다. 백그라운드 작업은 재부팅을 넘기지 못하고, 세션 작업 폴더, 승인(24시간),
명령 이력은 유지됩니다. 그래서 `agentprobe history`가 재부팅 전에 무엇을 실행했고 어떤 명령이 재부팅으로 끊겼는지
그대로 보여 줍니다.

## 안전

agentprobe는 원격 PC에 명령 실행 권한을 넘기는 도구이므로, 의심받는 것을 전제로 설계했습니다.

- **허용은 그 PC에서, 명시적으로.** 테스트할 PC 앞의 사람이 exe를 실행하고 특정 개발 PC 한 대를 승인해야 합니다.
  승인 화면은 누구의 권한을 넘기는지 알려 주고, 관리자 권한이면 크게 표시합니다.
- **항상 보입니다.** 개발 PC가 연결된 동안 화면 왼쪽 위에 *Remote control active* 배지가 떠 있습니다.
  그 PC에서도 원격에서도 끌 수 없습니다. 화면 캡처에서 제외되고 클릭이 통과하므로 테스트 중인 앱을 방해하지 않습니다.
  콘솔 창에는 실행되는 명령이 실시간으로 나열됩니다.
- **그 PC의 사람이 "지금은 안 돼"라고 할 수 있습니다.** 그 PC의 agentprobe 창에서 스페이스바를 누르면 Enter를 누를 때까지
  원격 명령이 멈춥니다. 새 명령은 모두 거절되고 에이전트에게 이유를 알려 주며, 세션은 유지됩니다. agentprobe의 입력 도구는
  그 창에는 동작하지 않고, 진행 중이던 입력도 그 창이 앞으로 나오면 멈춥니다.
- **기록이 남습니다.** 모든 명령, 파일 전송, 스크린샷, 입력 동작이 exe 옆의 `agentprobe-activity.log`에 추가됩니다.
  이 파일을 고치거나 지우는 원격 명령은 없고, `end --remove-exe`도 일부러 이 파일은 남깁니다.
- **비밀번호가 아니라 키.** 개발 PC마다 서명 키가 있습니다(SSH 호스트 키와 같은 개념). 테스트할 PC는 승인한 키를
  기억하므로, 다른 기기가 세션을 가로채거나 나중에 그 개발 PC인 척할 수 없습니다. 통신은 연결마다 새로 만든 키로
  암호화합니다(ECDH P-256, AES-256-GCM).
- **테스트할 PC는 아무 포트도 열지 않습니다.** 바깥으로 나가는 연결만 합니다.
- **개발 PC는 한 번에 한 대.** 테스트할 PC는 정확히 한 대의 개발 PC에만 연결되고, 한 PC에서 agentprobe는 하나만
  공유할 수 있습니다. 두 번째로 실행하면 시작을 거부합니다.
- **스스로 정리합니다.** `agentprobe end`나 창 닫기는 시작한 것을 멈추고, 작업 폴더를 지우고, agentprobe가 바꾼
  설정을 되돌립니다.

막아 주지 못하는 것: 승인된 개발 PC 그 자체입니다. 그 PC(와 에이전트)를 다루는 사람은 승인한 사용자가 테스트할
PC에서 할 수 있는 모든 일을 할 수 있습니다. 믿을 수 있는 기기만, 믿을 수 있는 네트워크에서 승인하세요.
무인 실험실에서는 `agentprobe share --trust 3F9A-21C4-88D0-B7E1`로 개발 PC 키 하나만 묻지 않고 받아들일 수 있습니다.
`--yes`는 처음 응답한 PC를 받아들이므로 격리된 네트워크에서만 쓰세요.

원격 명령을 받는 서명 없는 실행 파일은 백신의 휴리스틱에 악성코드처럼 보입니다. 조직 안에 배포한다면 서명하세요.

## 32비트와 64비트

`agentprobe.exe`는 64비트 프로그램이고 64비트 Windows 10/11이 필요합니다. 32비트와 64비트 프로세스를 모두 진단합니다.
32비트 앱의 경우 `deps`는 `SysWOW64`에서 DLL을 찾고 x86/x64 불일치를 알려 주며, `exec --shell ps32`와
`reg --view 32`는 32비트 프로세스가 보는 환경을 보여 줍니다. `dump`는 64비트 도구로 뜬 32비트 프로세스 덤프를
WinDbg에서 열 때 필요한 추가 단계를 안내합니다.

## 요구 사항과 한계

- 양쪽 PC 모두 Windows 10(1809 이상) 또는 Windows 11, x64. .NET 런타임은 필요 없습니다.
- 자동 검색은 같은 네트워크에서 됩니다. 아니면 주소를 직접 줍니다. 아직 중계 서버가 없어서, 테스트할 PC가
  개발 PC에 직접 닿을 수 있어야 합니다(LAN 또는 VPN).
- 명령은 테스트할 PC에서 agentprobe를 실행한 사용자의 권한으로 돕니다. 몇몇 도구는 *관리자 권한으로 실행*이
  필요합니다: `wpr`, `crashdumps on`, 메모리 페이지 목록, 다른 사용자의 프로세스.
- 보안 데스크톱(UAC 창, 잠금 화면)은 캡처하거나 클릭할 수 없습니다.
- 언어: 대시보드와 테스트할 PC에서 사람이 읽는 화면(승인 창, 배지)은 한국어와 영어를 지원하며 Windows 표시 언어를
  따릅니다. 대시보드에서 바꾸거나 `--lang en|ko`로 시작할 수 있습니다. 도구가 테스트할 PC에서 가져오는 출력과 활동 로그는 어느 PC에서나
  에이전트가 같은 글을 읽도록 항상 영어입니다. 명령줄이 스스로 하는 말(`list`, `help`, 안내와 오류)은 한국어
  Windows의 콘솔에서 사람이 볼 때는 한국어이고, 에이전트처럼 출력을 파이프로 받으면 영어입니다. 셸이 의사 터미널인
  에이전트는 `AGENTPROBE_LANG=en`을 지정하면 됩니다.
- Windows 11에서 개발하고 테스트했습니다. 버전 0.x: 프로토콜이 바뀔 수 있으므로 양쪽에 같은 버전을 쓰세요
  (연결된 PC의 버전이 다르면 `agentprobe list`가 알려 주고, `agentprobe -t <이름> update`로 맞춥니다).

## 결과를 믿을 수 있는가

틀린 값을 보여 주는 디버깅 도구는 없는 것보다 나쁩니다. 그래서 저장소에 검증 스크립트를 두었습니다. agentprobe의
출력을 Windows 자체의 값, 그리고 정답을 아는 상황과 대조합니다.

```powershell
.\tests\verify.ps1 -Session            # 약 4분. 누군가 쓰고 있는 PC에서는 -SkipInput 추가
```

프로세스 메모리, 핸들·스레드 수, 시스템 메모리, 포트, 파일 해시를 Windows가 보고하는 값과 비교합니다. 70MB 로그의
맨 끝 줄을 검색하고, 일부러 누수를 내는 프로세스를 띄워 누수 판정과 증가율이 맞는지(그리고 2분의 근거가 쌓인 뒤에만
판정하는지) 확인합니다. 테스트용 창을 UI 자동화와 실제 키보드·마우스 입력으로 조작하고, 프로세스를 종료했을 때 자식이
살아 있는지 보고, 파일과 폴더를 왕복 전송하고, 연결을 끊은 뒤 결과를 되찾습니다. 코드를 바꾼 뒤에는 실제로 쓸 PC에서
돌려 보세요.

원리상 근사치인 결과는 그렇다고 밝힙니다. `stacks`의 함수 이름은 PDB가 없으면 내보내기 테이블에서 오므로 내부
함수는 모듈+오프셋으로 표시하고, 누수 판정은 통계적 판단이며, `trace`는 부하 때문에 이벤트가 유실되면 그 사실을
알립니다.

## 빌드

```powershell
dotnet build                                            # .NET 10 SDK
dotnet publish src/AgentProbe -c Release -o publish     # 네이티브 exe 하나 (VS의 "C++를 사용한 데스크톱 개발" 워크로드 필요)
```

`tools/make-icon.py`로 아이콘을 다시 만들 수 있습니다.

릴리스는 GitHub Actions가 만듭니다. 태그를 푸시하거나(`git tag v0.2.0; git push origin v0.2.0`) `build` 워크플로를
버전을 입력해 직접 실행하면, 그 버전으로 exe를 빌드하고 검증 스크립트를 돌린 뒤 `agentprobe.exe`와 SHA-256
체크섬이 첨부된 릴리스를 게시합니다.

## 라이선스

[MIT](LICENSE). 대시보드에는 SIL Open Font License 1.1로 배포되는 [Pretendard](https://github.com/orioncactus/pretendard)
글꼴이 들어 있습니다(`src/AgentProbe/Controller/fonts/OFL.txt`).
