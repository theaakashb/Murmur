import AppKit
import SwiftUI
import Charts

// Home and Insights pages, with the streak calendar.

extension SettingsView {
    func page<Content: View>(_ title: String, _ lede: String, @ViewBuilder _ content: () -> Content) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Text(title).font(WindowType.display(64)).tracking(-1.28)
                Text(lede).font(WindowType.body(16)).foregroundStyle(Palette.ink70).padding(.top, 10)
                content()
            }
            .padding(EdgeInsets(top: 16, leading: 24, bottom: 24, trailing: 24))
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    var home: some View {
        page("Home", "Dictate anywhere the cursor is.") {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                help("Transcription: \(controller.transcriptionRoute == .local ? "On this Mac" : controller.cloudTranscriber.displayName) · Cleanup: \((cleanupStyle.wrappedValue.needsModel ? controller.cleanupChoice : .localRules).displayName)")
                SettingInfoButton(text: ([controller.routeDisclosure(.transcription), controller.routeDisclosure(.cleanup)].compactMap { $0 } + ["On-device routes process data on this Mac. Custom providers use the server configured in Settings."]).joined(separator: "\n\n"))
                    .accessibilityLabel("Where dictation data goes")
            }.padding(.top, 12)
            HStack(spacing: 4) {
                eyebrow("Shortcut")
                SettingInfoButton(text: controller.pressToToggle ? "Press to start, press again to stop. Escape cancels." : "Hold to record, or double-press to keep recording until you press again. Escape cancels.")
                    .accessibilityLabel("How to use the shortcut")
            }.padding(.top, 28)
            HStack(alignment: .center, spacing: 20) {
                Text(controller.shortcutName).font(WindowType.body(28, weight: .bold)).tracking(-0.8)
                Spacer()
                if controller.phase == .listening {
                    Button("Finish Dictation", action: controller.stopRecording).buttonStyle(InkButtonStyle(kind: .filled))
                } else {
                    Button("Start Dictation", action: controller.startDictationFromHome).buttonStyle(InkButtonStyle(kind: .filled))
                        .disabled(!setupComplete || controller.isBusy)
                        .help("Murmur steps aside so your words go to the app you were using. Press the shortcut to finish.")
                }
            }
            .padding(.vertical, 18).padding(.top, 8)
            HStack(alignment: .center, spacing: 20) {
                HStack(spacing: 4) {
                    Text("Meeting notes").font(WindowType.body(17, weight: .semibold))
                    SettingInfoButton(text: controller.meetingRecorder.live == nil ? "Transcribe a call on this Mac: your microphone as Me, the call’s audio as Them." : "Recording now. The live panel shows the transcript.")
                        .accessibilityLabel("About meeting notes")
                }
                Spacer()
                if controller.meetingRecorder.live == nil { MeetingStartMenu(controller: controller) }
                else { Button("Open Meetings") { screen = .meetings }.buttonStyle(InkButtonStyle(kind: .outline)) }
            }
            .padding(.vertical, 18).padding(.top, 16)

            HStack(alignment: .firstTextBaseline) {
                eyebrow("Ready to dictate")
                Spacer()
                if !setupComplete { Button("Open Welcome Guide") { screen = .setup }.buttonStyle(InkButtonStyle(kind: .plain)) }
            }
            .padding(.top, 28)
            VStack(spacing: 0) {
                ForEach(Permission.allCases, id: \.self) { permissionRow($0) }
                permissionRow("Speech model", allowed: modelSatisfied, actionTitle: "Open Transcription") { section = .transcription; screen = .settings }
            }
            .overlay(alignment: .top) { rule }.padding(.top, 8)

            HStack(alignment: .firstTextBaseline) {
                eyebrow("Recent dictations")
                Spacer()
                if !controller.history.isEmpty { Button("Show All in History →") { screen = .history }.buttonStyle(InkButtonStyle(kind: .plain)) }
            }
            .padding(.top, 28)
            VStack(spacing: 0) {
                if controller.history.isEmpty {
                    help(controller.historyMode == .on ? "Nothing yet. Your dictations appear here and in History." : "History is off, so dictations are kept only until Murmur quits.")
                        .padding(.vertical, 16).frame(maxWidth: .infinity, alignment: .leading)
                }
                ForEach(controller.history.prefix(5)) { entry in
                    Button { selectedID = entry.id; searchText = ""; screen = .history } label: {
                        HStack(alignment: .top, spacing: 16) {
                            Text(entry.recordedAt, format: .relative(presentation: .named))
                                .font(.system(size: 13, design: .monospaced)).foregroundStyle(Palette.ink70).lineLimit(1).frame(width: 120, alignment: .leading)
                            Text((entry.finalText.isEmpty ? entry.rawText : entry.finalText).replacingOccurrences(of: "\n", with: " "))
                                .font(WindowType.body(15)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                            if let label = Self.statusLabel(entry.status) { mono(label, size: 11, color: Palette.accentInk) }
                        }
                        .padding(.vertical, 12).contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).overlay(alignment: .bottom) { softRule }
                }
            }
            .overlay(alignment: .top) { rule }.padding(.top, 8)
            Button("Replay Welcome Guide", action: replayGuide)
                .buttonStyle(InkButtonStyle(kind: .outline)).padding(.top, 28)
        }
    }

    private var keptFor: String {
        switch controller.retention {
        case .sixMonths: return "six months"
        case .forever: return "until you delete them"
        case .thirtyDays: return "30 days"
        }
    }

    var insightsView: some View {
        page("Insights", "Counted on this Mac from the dictations History keeps. Nothing is sent anywhere.") {
            switch controller.insights {
            case .historyOff:
                insightsNotice("History is off", "Insights counts only what History keeps on this Mac, so there is nothing to count. Turn History on in History & Privacy to see your words, pace and streaks.")
            case .empty:
                insightsNotice("Nothing to count", "History keeps dictations for \(keptFor). It has none right now: either you have not dictated yet, or older dictations have expired. Insights counts only what History still has.")
            case .stats(let stats):
                insightsStats(stats)
            }
        }
    }

    private func insightsNotice(_ title: String, _ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title).font(WindowType.body(22, weight: .semibold))
            help(detail)
            Button("Open History & Privacy") { section = .privacy; screen = .settings }.buttonStyle(InkButtonStyle(kind: .outline))
        }
        .padding(24).frame(maxWidth: .infinity, alignment: .leading).background(Palette.accent16).padding(.top, 28)
    }

    private func insightsStats(_ s: Insights.Stats) -> some View {
        let timed = "Based on \(s.timedDictations) of \(s.dictations) dictations with a recorded duration. Includes pauses."
        return VStack(alignment: .leading, spacing: 24) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: 12)], spacing: 12) {
                insightMetric("Words dictated", s.words.formatted(), "Words in the final transcripts still in History.")
                insightMetric("Dictations", s.dictations.formatted(), "Dictations with a nonempty final transcript.")
                insightMetric("Recording time", s.recordingSeconds.map(Insights.duration) ?? "—", timed)
                insightMetric("Speaking pace", s.wordsPerMinute.map { "\(Int($0.rounded())) WPM" } ?? "—", "Five-character words per minute. " + timed)
                insightMetric("Est. time saved", s.secondsSaved.map(Insights.duration) ?? "—", "Estimated against typing at \(Int(Insights.typingWPM.rounded())) WPM; not your measured typing speed. " + timed)
                insightMetric("Words / dictation", s.averageWords.formatted(.number.precision(.fractionLength(1))), "Average final word count per dictation.")
                insightMetric("Words changed", s.wordsChanged.formatted(), "Differences between raw and final text, including cleanup, dictionary spellings and History edits. Not an accuracy score.")
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 280), alignment: .top)], alignment: .leading, spacing: 28) {
                dailyActivity(s).frame(height: 300, alignment: .top)
                VStack(alignment: .leading, spacing: 12) {
                    insightHeading("Streak", "Calendar shows the last five weeks. A day counts after one dictation in this Mac’s time zone. Streak totals use all retained History.")
                    HStack(spacing: 24) {
                        VStack(alignment: .leading) {
                            Text("\(s.currentStreak) \(s.currentStreak == 1 ? "day" : "days")").font(WindowType.body(22, weight: .bold))
                            mono("Current", size: 11, color: Palette.ink70)
                        }
                        VStack(alignment: .leading) {
                            Text("\(s.longestStreak) \(s.longestStreak == 1 ? "day" : "days")").font(WindowType.body(22, weight: .bold))
                            mono("Longest retained", size: 11, color: Palette.ink70)
                        }
                    }
                    StreakCalendar(days: s.days, weeks: 5,
                                   cutoff: controller.retention.cutoff())
                }.frame(maxWidth: .infinity, alignment: .leading).frame(height: 300, alignment: .top)
                hourlyActivity(s).frame(height: 300, alignment: .top)
                VStack(alignment: .leading, spacing: 12) {
                    insightHeading("Top apps", "Ranked by words in retained History. \(s.withoutApp) dictations have no destination app recorded.")
                    if s.apps.isEmpty { help("App usage appears after a destination app is recorded.") }
                    ForEach(s.apps.prefix(6), id: \.app) { item in
                        VStack(alignment: .leading, spacing: 5) {
                            HStack {
                                Text(appName(item.app)).font(WindowType.body(14)).lineLimit(1)
                                Spacer()
                                Text("\(item.words.formatted()) words").font(WindowType.body(12)).foregroundStyle(Palette.ink70)
                            }
                            GeometryReader { geometry in
                                Rectangle().fill(Palette.accent).frame(width: geometry.size.width * Double(item.words) / Double(max(s.apps.first?.words ?? 1, 1)))
                            }.frame(height: 5).background(Palette.ink20)
                        }
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel("\(appName(item.app)), \(item.words) words, \(item.count) dictations")
                    }
                }.frame(maxWidth: .infinity, alignment: .leading).frame(height: 300, alignment: .top)
            }
            help("Based on retained History · kept for \(keptFor). Older entries may lack duration or app data.", color: Palette.ink64)
        }.padding(.top, 20)
    }

    private func insightMetric(_ title: String, _ value: String, _ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            insightHeading(title, detail)
            Text(value).font(WindowType.body(28, weight: .bold)).lineLimit(1).minimumScaleFactor(0.7)
        }
        .frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 12)
        .overlay(alignment: .bottom) { softRule }
    }

    private func insightHeading(_ title: String, _ detail: String) -> some View {
        HStack(spacing: 4) {
            eyebrow(title)
            SettingInfoButton(text: detail).accessibilityLabel("About " + title)
        }
    }

    private func dailyActivity(_ s: Insights.Stats) -> some View {
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: Date())
        let days = (0..<30).compactMap { calendar.date(byAdding: .day, value: $0 - 29, to: today) }
        return VStack(alignment: .leading, spacing: 12) {
            insightHeading("Daily activity", "Words per day over the last 30 days, in this Mac’s time zone. Counts include only retained History.")
            Chart(days, id: \.self) { day in
                BarMark(x: .value("Day", day, unit: .day), y: .value("Words", s.dailyWords[day, default: 0]))
                    .foregroundStyle(Palette.accent)
                    .accessibilityLabel(day.formatted(date: .abbreviated, time: .omitted))
                    .accessibilityValue("\(s.dailyWords[day, default: 0]) words")
            }
            .chartXAxis { AxisMarks(values: .stride(by: .day, count: 10)) { _ in AxisValueLabel(format: .dateTime.month(.abbreviated).day()) } }
            .frame(height: 264)
        }
    }

    private func hourlyActivity(_ s: Insights.Stats) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            insightHeading("Busiest hours", "Dictations started in each hour, using this Mac’s current time zone. Includes all retained History; not recording duration.")
            Chart(0..<24, id: \.self) { hour in
                BarMark(x: .value("Hour", hour), y: .value("Dictations", s.hourlyDictations[hour]))
                    .foregroundStyle(Palette.accent)
                    .accessibilityLabel("\(hour):00–\(hour):59")
                    .accessibilityValue("\(s.hourlyDictations[hour]) dictations")
            }
            .chartXScale(domain: -0.5...23.5)
            .chartXAxis { AxisMarks(values: [0, 6, 12, 18]) { value in
                AxisValueLabel { if let hour = value.as(Int.self) { Text(hour == 0 ? "12am" : hour < 12 ? "\(hour)am" : hour == 12 ? "12pm" : "\(hour - 12)pm") } }
            } }
            .frame(height: 264)
        }
    }

}

/// Weeks as rows, Monday to Sunday across; days before History's cutoff are faded
/// out, since their dictations have expired.
private struct StreakCalendar: View {
    let days: Set<Date>
    let weeks: Int
    let cutoff: Date?
    var body: some View {
        var calendar = Calendar.current; calendar.firstWeekday = 2
        let today = calendar.startOfDay(for: Date())
        let thisWeek = calendar.dateInterval(of: .weekOfYear, for: today)?.start ?? today
        let first = calendar.date(byAdding: .weekOfYear, value: -(weeks - 1), to: thisWeek) ?? thisWeek
        return LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 7), spacing: 4) {
            ForEach(0..<7, id: \.self) { weekday in
                Text(calendar.shortStandaloneWeekdaySymbols[(weekday + 1) % 7])
                    .font(WindowType.body(11)).foregroundStyle(Palette.ink70)
                    .frame(maxWidth: .infinity).padding(.bottom, 4)
            }
            ForEach(0..<(weeks * 7), id: \.self) { offset in
                let day = calendar.date(byAdding: .day, value: offset, to: first) ?? first
                let on = days.contains(day)
                Text("\(calendar.component(.day, from: day))")
                    .font(WindowType.body(11, weight: on ? .semibold : .regular))
                    .foregroundStyle(on ? Color.white : Palette.ink70)
                    .frame(maxWidth: .infinity).frame(height: 26)
                    .background(on ? Palette.accent : Palette.ink.opacity(0.06), in: Capsule())
                    .overlay(Capsule().strokeBorder(day == today ? Palette.ink : Color.clear, lineWidth: 1.5))
                    .opacity(day > today || cutoff.map { day < calendar.startOfDay(for: $0) } == true ? 0.3 : 1)
                    .help(day.formatted(date: .abbreviated, time: .omitted) + (on ? ": dictated" : ": no retained dictation"))
            }
        }
        .accessibilityElement().accessibilityLabel("Dictation days")
        .accessibilityValue("\(days.filter { $0 >= first && $0 <= today }.count) days with dictation in the last \(weeks) weeks")
    }
}
