import { useEffect, useState } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { api, getToken, setToken, type Identity } from './lib/api'
import { AuthScreens } from './features/auth/AuthScreens'
import { PatientPortal } from './features/patient/PatientPortal'
import { AdminConsole } from './features/admin/AdminConsole'
import { Shell } from './components/Shell'
import { Today } from './features/Today'
import { Encounter } from './features/Encounter'
import { NoteReview } from './features/NoteReview'
import { Inbox } from './features/Inbox'
import { Patient, PatientList } from './features/Patient'
import { Schedule } from './features/Schedule'
import { Admin, InsightsScreen } from './features/Admin'

/**
 * Three products behind one sign-in.
 *
 * Which shell renders is decided by `surface`, computed on the SERVER from the role.
 * Deriving it on the client would let the two disagree, and a patient briefly seeing a
 * clinical chrome — even with every API call still refused — is not acceptable.
 */
export default function App() {
  const [me, setMe] = useState<Identity | null>(null)
  const [checking, setChecking] = useState(true)

  useEffect(() => {
    if (!getToken()) {
      setChecking(false)
      return
    }
    api
      .get<Identity>('/v1/auth/me')
      .then(setMe)
      .catch(() => setToken(null))
      .finally(() => setChecking(false))
  }, [])

  if (checking) return null
  if (!me) return <AuthScreens onSignedIn={setMe} />

  if (me.surface === 'patient') return <PatientPortal me={me} />
  if (me.surface === 'admin') return <AdminConsole me={me} />

  return <ClinicalWorkspace me={me} />
}

function ClinicalWorkspace({ me }: { me: Identity }) {
  return (
    <BrowserRouter>
      <Shell me={me}>
        <Routes>
          <Route path="/" element={<Navigate to="/today" replace />} />
          <Route path="/today" element={<Today me={me} />} />
          <Route path="/encounter/:id" element={<Encounter />} />
          <Route path="/note/:id" element={<NoteReview />} />
          <Route path="/patients" element={<PatientList />} />
          <Route path="/patients/:id" element={<Patient />} />
          <Route path="/schedule" element={<Schedule />} />
          <Route path="/inbox" element={<Inbox />} />
          <Route path="/insights" element={<InsightsScreen />} />
          <Route path="/admin" element={<Admin />} />
          <Route path="*" element={<Navigate to="/today" replace />} />
        </Routes>
      </Shell>
    </BrowserRouter>
  )
}
